using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.IoT;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Deployment;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AwsApigatewayv2Authorizers;
using Amazon.CDK.AwsApigatewayv2Integrations;
using Constructs;
using CfnScheduleGroup = Amazon.CDK.AWS.Scheduler.CfnScheduleGroup;
using CfnScheduleGroupProps = Amazon.CDK.AWS.Scheduler.CfnScheduleGroupProps;
using CfFunction = Amazon.CDK.AWS.CloudFront.Function;
using Distribution = Amazon.CDK.AWS.CloudFront.Distribution;
using DynamoAttribute = Amazon.CDK.AWS.DynamoDB.Attribute;
using EventBus = Amazon.CDK.AWS.Events.EventBus;
using EventBusProps = Amazon.CDK.AWS.Events.EventBusProps;
using Function = Amazon.CDK.AWS.Lambda.Function;
using FunctionProps = Amazon.CDK.AWS.Lambda.FunctionProps;
using HttpMethod = Amazon.CDK.AWS.Apigatewayv2.HttpMethod;
using LogGroupProps = Amazon.CDK.AWS.Logs.LogGroupProps;

namespace GridSafety.Infra;

public sealed class GridSafetyStackProps : StackProps
{
    public required string Stage { get; init; }
    public string? OpsEmail { get; init; }

    /// <summary>Monthly AWS Budget in USD that emails <see cref="OpsEmail"/>; 0 disables it.</summary>
    public double MonthlyBudgetUsd { get; init; } = 5;

    /// <summary>
    /// Custom domain for the site, e.g. gridsafety.example.com. Needs <see cref="CertificateArn"/>: an ACM
    /// certificate for that name in us-east-1 (CloudFront only uses certificates from there). DNS is managed
    /// outside AWS, so point a CNAME at the <c>CloudFrontDomain</c> output.
    /// </summary>
    public string? DomainName { get; init; }
    public string? CertificateArn { get; init; }
}

/// <summary>
/// One stage of GridSafety:
///
///   Drone ─MQTT─► IoT Core ─(topic rule)─► DroneIngest λ ─► DynamoDB (single table)
///                                              │                 │ stream (SK=META only)
///                                              │                 ▼
///                                              └──────────► EventBridge bus ◄── StreamPublisher λ
///                                                             │
///          ┌──────────────────────────┬───────────────────────┼────────────────────────┐
///          ▼                          ▼                       ▼                        ▼
///   SlaScheduler λ             SQS ─► AlertNotifier λ   MissionReport λ          Archive (30d)
///      │ at() schedule                 │                      │
///      ▼                               ▼                      ▼
///   EventBridge Scheduler ─► SlaEscalation λ ─► SNS ops    S3 reports
///
///   Browser ─► CloudFront ─┬─► S3 (React ops console)
///                          └─► /api/* ─► API Gateway (Cognito JWT) ─► Lambda (ASP.NET Core)
/// </summary>
public sealed class GridSafetyStack : Stack
{
    private const string EventSource = "gridsafety";

    /// <summary>Steady-state requests per second for the whole HTTP API; extra requests get 429.</summary>
    public const double ApiRateLimitPerSecond = 10;
    public const double ApiBurstLimit = 20;
    private const string Namespace = "GridSafety";

    // .NET 10 managed runtime on Graviton.
    private static readonly Runtime DotNet = new("dotnet10", RuntimeFamily.DOTNET_CORE);

    public GridSafetyStack(Construct scope, string id, GridSafetyStackProps props) : base(scope, id, props)
    {
        var isProd = props.Stage == "prod";
        var removal = isProd ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY;
        var repoRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".."));
        string Artifact(string name) => Path.Combine(repoRoot, "artifacts", name);

        // ───────────── Data ─────────────
        var table = new Table(this, "Table", new TableProps
        {
            PartitionKey = new DynamoAttribute { Name = "PK", Type = AttributeType.STRING },
            SortKey = new DynamoAttribute { Name = "SK", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Stream = StreamViewType.NEW_AND_OLD_IMAGES,
            TimeToLiveAttribute = "ExpiresAt", // telemetry ages out after 30 days; idempotency keys after 1 day
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = isProd },
            DeletionProtection = isProd,
            RemovalPolicy = removal,
        });
        table.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "GSI1",
            PartitionKey = new DynamoAttribute { Name = "GSI1PK", Type = AttributeType.STRING },
            SortKey = new DynamoAttribute { Name = "GSI1SK", Type = AttributeType.STRING },
        });

        var reports = new Bucket(this, "Reports", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            Versioned = isProd,
            RemovalPolicy = removal,
            AutoDeleteObjects = !isProd,
            LifecycleRules = new ILifecycleRule[]
            {
                // Inspection records are compliance evidence: keep them, but cheaply.
                new LifecycleRule
                {
                    Transitions = new ITransition[] { new Transition { StorageClass = Amazon.CDK.AWS.S3.StorageClass.INFREQUENT_ACCESS, TransitionAfter = Duration.Days(90) } },
                },
            },
        });

        var site = new Bucket(this, "Site", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            Encryption = BucketEncryption.S3_MANAGED,
            EnforceSSL = true,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
        });

        // ───────────── Auth ─────────────
        var userPool = new UserPool(this, "UserPool", new UserPoolProps
        {
            SelfSignUpEnabled = false, // utility staff are provisioned, not self-registered
            SignInAliases = new SignInAliases { Email = true },
            AutoVerify = new AutoVerifiedAttrs { Email = true },
            StandardAttributes = new StandardAttributes { Email = new StandardAttribute { Required = true, Mutable = true } },
            PasswordPolicy = new PasswordPolicy { MinLength = 12, RequireSymbols = true },
            Mfa = Mfa.OPTIONAL,
            MfaSecondFactor = new MfaSecondFactor { Otp = true, Sms = false },
            AccountRecovery = AccountRecovery.EMAIL_ONLY,
            RemovalPolicy = removal,
        });
        new CfnUserPoolGroup(this, "SupervisorsGroup", new CfnUserPoolGroupProps
        {
            UserPoolId = userPool.UserPoolId,
            GroupName = "Supervisors",
            Description = "May dismiss findings",
        });
        var domain = userPool.AddDomain("Domain", new UserPoolDomainOptions
        {
            CognitoDomain = new CognitoDomainOptions { DomainPrefix = $"gridsafety-{props.Stage}-{Account}" },
        });

        // ───────────── Events + paging ─────────────
        var bus = new EventBus(this, "Bus", new EventBusProps { EventBusName = $"gridsafety-{props.Stage}" });
        bus.Archive("Archive", new BaseArchiveProps
        {
            Description = "Replayable history of mission, finding and drone-alert events",
            EventPattern = new EventPattern { Source = new[] { EventSource } },
            Retention = Duration.Days(30),
        });

        var opsTopic = new Topic(this, "OpsPages", new TopicProps { DisplayName = $"GridSafety {props.Stage} ops" });
        var alarmTopic = new Topic(this, "Alarms", new TopicProps { DisplayName = $"GridSafety {props.Stage} platform alarms" });
        if (props.OpsEmail is { Length: > 0 } opsEmail)
        {
            opsTopic.AddSubscription(new EmailSubscription(opsEmail));
            alarmTopic.AddSubscription(new EmailSubscription(opsEmail));
        }

        // ───────────── Cost guardrail ─────────────
        // A monthly AWS Budget that emails the ops address before the bill surprises anyone. Budgets cover the
        // whole account (no cost-allocation tags to activate first), so it also catches spend outside this stack.
        // Alerts only: nothing is stopped. Set -c monthlyBudgetUsd=0 to skip it (e.g. on a second stage in the same account).
        if (props.OpsEmail is { Length: > 0 } budgetEmail && props.MonthlyBudgetUsd > 0)
        {
            CfnBudget.NotificationWithSubscribersProperty Alert(string type, double percent) => new()
            {
                Notification = new CfnBudget.NotificationProperty
                {
                    NotificationType = type,                // ACTUAL spend so far, or FORECASTED for the month
                    ComparisonOperator = "GREATER_THAN",
                    Threshold = percent,
                    ThresholdType = "PERCENTAGE",
                },
                Subscribers = new[] { new CfnBudget.SubscriberProperty { SubscriptionType = "EMAIL", Address = budgetEmail } },
            };

            new CfnBudget(this, "MonthlyBudget", new CfnBudgetProps
            {
                Budget = new CfnBudget.BudgetDataProperty
                {
                    BudgetName = $"GridSafety-{props.Stage}-monthly",
                    BudgetType = "COST",
                    TimeUnit = "MONTHLY",
                    BudgetLimit = new CfnBudget.SpendProperty { Amount = props.MonthlyBudgetUsd, Unit = "USD" },
                },
                NotificationsWithSubscribers = new[]
                {
                    Alert("ACTUAL", 85),
                    Alert("ACTUAL", 100),
                    Alert("FORECASTED", 100), // usually warns days before the money is actually spent
                },
            });
        }

        // ───────────── Lambdas ─────────────
        Function NewFunction(string name, string handler, string asset, int memory, Dictionary<string, string> env, int timeoutSeconds = 30)
        {
            env["POWERTOOLS_SERVICE_NAME"] = $"gridsafety-{name.ToLowerInvariant()}";
            env["POWERTOOLS_METRICS_NAMESPACE"] = Namespace;
            return new Function(this, name, new FunctionProps
            {
                Runtime = DotNet,
                Architecture = Architecture.ARM_64,
                Handler = handler,
                Code = Code.FromAsset(Artifact(asset)),
                MemorySize = memory,
                Timeout = Duration.Seconds(timeoutSeconds),
                Tracing = Tracing.ACTIVE,
                Environment = env,
                LogGroup = new LogGroup(this, $"{name}Logs", new LogGroupProps
                {
                    Retention = RetentionDays.ONE_MONTH,
                    RemovalPolicy = RemovalPolicy.DESTROY,
                }),
            });
        }

        const string fn = "GridSafety.Functions::GridSafety.Functions.";

        var apiFn = NewFunction("Api", "GridSafety.Api", "api", 1024, new()
        {
            ["GridSafety__TableName"] = table.TableName,
            ["GridSafety__ReportsBucket"] = reports.BucketName,
        }, timeoutSeconds: 15);
        table.GrantReadWriteData(apiFn);
        reports.GrantRead(apiFn); // presigned GETs are signed with the function's credentials

        // Drone telemetry: IoT Core topic rule ─► ingest Lambda.
        var ingestFn = NewFunction("DroneIngest", $"{fn}DroneIngestFunction::HandleAsync", "functions", 1024, new()
        {
            ["TABLE_NAME"] = table.TableName,
            ["EVENT_BUS_NAME"] = bus.EventBusName,
        });
        table.GrantReadWriteData(ingestFn);
        bus.GrantPutEventsTo(ingestFn);

        var iotErrors = new LogGroup(this, "IotRuleErrors", new LogGroupProps
        {
            Retention = RetentionDays.TWO_WEEKS,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });
        var iotErrorRole = new Role(this, "IotErrorRole", new RoleProps { AssumedBy = new ServicePrincipal("iot.amazonaws.com") });
        iotErrors.GrantWrite(iotErrorRole);

        var topicRule = new CfnTopicRule(this, "DroneTelemetryRule", new CfnTopicRuleProps
        {
            RuleName = $"gridsafety_{props.Stage}_drone_ingest",
            TopicRulePayload = new CfnTopicRule.TopicRulePayloadProperty
            {
                Description = "Route drone telemetry, findings and landings to the ingest Lambda",
                AwsIotSqlVersion = "2016-03-23",
                // droneId comes from the topic (locked down by the device policy), not the payload.
                Sql = "SELECT *, topic(3) AS droneId, topic(4) AS kind FROM 'gridsafety/drones/+/+'",
                RuleDisabled = false,
                Actions = new object[]
                {
                    new CfnTopicRule.ActionProperty { Lambda = new CfnTopicRule.LambdaActionProperty { FunctionArn = ingestFn.FunctionArn } },
                },
                ErrorAction = new CfnTopicRule.ActionProperty
                {
                    CloudwatchLogs = new CfnTopicRule.CloudwatchLogsActionProperty { LogGroupName = iotErrors.LogGroupName, RoleArn = iotErrorRole.RoleArn },
                },
            },
        });
        ingestFn.AddPermission("AllowIotRule", new Permission
        {
            Principal = new ServicePrincipal("iot.amazonaws.com"),
            SourceArn = topicRule.AttrArn,
        });

        // Real drones authenticate with X.509 certs; each Thing may only publish under its own name.
        new Amazon.CDK.AWS.IoT.CfnPolicy(this, "DronePolicy", new Amazon.CDK.AWS.IoT.CfnPolicyProps
        {
            PolicyName = $"gridsafety-{props.Stage}-drone",
            PolicyDocument = new Dictionary<string, object>
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:Connect",
                        ["Resource"] = $"arn:{Partition}:iot:{Region}:{Account}:client/${{iot:Connection.Thing.ThingName}}",
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:Publish",
                        ["Resource"] = $"arn:{Partition}:iot:{Region}:{Account}:topic/gridsafety/drones/${{iot:Connection.Thing.ThingName}}/*",
                    },
                },
            },
        });

        // The simulator publishes over HTTPS with IAM creds; attach this policy to whoever runs it.
        var simulatorPolicy = new ManagedPolicy(this, "SimulatorPolicy", new ManagedPolicyProps
        {
            Description = "Lets the GridSafety drone simulator publish to IoT Core",
            Statements = new[]
            {
                new PolicyStatement(new PolicyStatementProps
                {
                    Actions = new[] { "iot:Publish" },
                    Resources = new[] { $"arn:{Partition}:iot:{Region}:{Account}:topic/gridsafety/drones/*" },
                }),
                new PolicyStatement(new PolicyStatementProps { Actions = new[] { "iot:DescribeEndpoint" }, Resources = new[] { "*" } }),
            },
        });

        // Outbox: table stream ─► EventBridge. Only entity META rows (missions, findings) produce events;
        // telemetry, drone shadows and idempotency records never invoke the publisher.
        var streamFn = NewFunction("StreamPublisher", $"{fn}StreamPublisher::HandleAsync", "functions", 512, new()
        {
            ["EVENT_BUS_NAME"] = bus.EventBusName,
        });
        bus.GrantPutEventsTo(streamFn);
        var streamDlq = new Queue(this, "StreamDlq", new QueueProps { RetentionPeriod = Duration.Days(14), EnforceSSL = true });
        streamFn.AddEventSource(new DynamoEventSource(table, new DynamoEventSourceProps
        {
            StartingPosition = StartingPosition.TRIM_HORIZON,
            BatchSize = 25,
            BisectBatchOnError = true,
            RetryAttempts = 5,
            ReportBatchItemFailures = true,
            OnFailure = new SqsDlq(streamDlq),
            Filters = new[]
            {
                FilterCriteria.Filter(new Dictionary<string, object>
                {
                    ["dynamodb"] = new Dictionary<string, object>
                    {
                        ["Keys"] = new Dictionary<string, object>
                        {
                            ["SK"] = new Dictionary<string, object> { ["S"] = new object[] { "META" } },
                        },
                    },
                }),
            },
        }));

        // Triage SLA: a one-time EventBridge Scheduler timer per finding; escalates if still untriaged.
        var scheduleGroup = new CfnScheduleGroup(this, "SlaGroup", new CfnScheduleGroupProps { Name = $"gridsafety-sla-{props.Stage}" });

        var escalationFn = NewFunction("SlaEscalation", $"{fn}SlaEscalation::HandleAsync", "functions", 512, new()
        {
            ["TABLE_NAME"] = table.TableName,
            ["OPS_TOPIC_ARN"] = opsTopic.TopicArn,
        });
        table.GrantReadWriteData(escalationFn);
        opsTopic.GrantPublish(escalationFn);

        var schedulerRole = new Role(this, "SchedulerRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("scheduler.amazonaws.com"),
            Description = "Lets EventBridge Scheduler invoke the SLA escalation",
        });
        escalationFn.GrantInvoke(schedulerRole);

        var slaFn = NewFunction("SlaScheduler", $"{fn}SlaScheduler::HandleAsync", "functions", 512, new()
        {
            ["SCHEDULE_GROUP"] = scheduleGroup.Name!,
            ["ESCALATION_FUNCTION_ARN"] = escalationFn.FunctionArn,
            ["SCHEDULER_ROLE_ARN"] = schedulerRole.RoleArn,
        });
        slaFn.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[] { "scheduler:CreateSchedule", "scheduler:UpdateSchedule", "scheduler:DeleteSchedule", "scheduler:GetSchedule" },
            Resources = new[] { $"arn:{Partition}:scheduler:{Region}:{Account}:schedule/{scheduleGroup.Name}/*" },
        }));
        schedulerRole.GrantPassRole(slaFn.GrantPrincipal);

        var eventsDlq = new Queue(this, "EventsDlq", new QueueProps { RetentionPeriod = Duration.Days(14), EnforceSSL = true });
        new Rule(this, "SlaRule", new RuleProps
        {
            EventBus = bus,
            Description = "Start or cancel triage SLA timers",
            EventPattern = new EventPattern
            {
                Source = new[] { EventSource },
                DetailType = new[] { "FindingCreated", "FindingStatusChanged" },
            },
            Targets = new IRuleTarget[] { new LambdaFunction(slaFn, new LambdaFunctionProps { DeadLetterQueue = eventsDlq, RetryAttempts = 4 }) },
        });

        // Paging: critical drone alerts and sev 4–5 findings ─► SQS (buffer, retries, DLQ) ─► notifier ─► SNS.
        var pageDlq = new Queue(this, "PageDlq", new QueueProps { RetentionPeriod = Duration.Days(14), EnforceSSL = true });
        var pageQueue = new Queue(this, "PageQueue", new QueueProps
        {
            VisibilityTimeout = Duration.Minutes(3),
            EnforceSSL = true,
            DeadLetterQueue = new DeadLetterQueue { Queue = pageDlq, MaxReceiveCount = 3 },
        });
        new Rule(this, "CriticalAlertRule", new RuleProps
        {
            EventBus = bus,
            Description = "Critical in-flight drone alerts",
            EventPattern = new EventPattern
            {
                Source = new[] { EventSource },
                DetailType = new[] { "DroneAlertRaised" },
                Detail = new Dictionary<string, object> { ["level"] = new[] { "Critical" } },
            },
            Targets = new IRuleTarget[] { new SqsQueue(pageQueue) },
        });
        new Rule(this, "SevereFindingRule", new RuleProps
        {
            EventBus = bus,
            Description = "Severity 4-5 findings need eyes immediately",
            EventPattern = new EventPattern
            {
                Source = new[] { EventSource },
                DetailType = new[] { "FindingCreated" },
                Detail = new Dictionary<string, object>
                {
                    ["severity"] = new object[] { new Dictionary<string, object> { ["numeric"] = new object[] { ">=", 4 } } },
                },
            },
            Targets = new IRuleTarget[] { new SqsQueue(pageQueue) },
        });

        var notifierFn = NewFunction("AlertNotifier", $"{fn}AlertNotifier::HandleAsync", "functions", 512, new()
        {
            ["TABLE_NAME"] = table.TableName,
            ["OPS_TOPIC_ARN"] = opsTopic.TopicArn,
        });
        table.GrantReadWriteData(notifierFn); // idempotency records
        opsTopic.GrantPublish(notifierFn);
        notifierFn.AddEventSource(new SqsEventSource(pageQueue, new SqsEventSourceProps { BatchSize = 10, ReportBatchItemFailures = true }));

        // Mission reports: completed/aborted mission ─► HTML report in S3.
        var reportFn = NewFunction("MissionReport", $"{fn}MissionReportGenerator::HandleAsync", "functions", 1024, new()
        {
            ["TABLE_NAME"] = table.TableName,
            ["REPORTS_BUCKET"] = reports.BucketName,
        }, timeoutSeconds: 60);
        table.GrantReadWriteData(reportFn);
        reports.GrantPut(reportFn);
        new Rule(this, "MissionEndedRule", new RuleProps
        {
            EventBus = bus,
            Description = "Generate the inspection report when a mission ends",
            EventPattern = new EventPattern
            {
                Source = new[] { EventSource },
                DetailType = new[] { "MissionStatusChanged" },
                Detail = new Dictionary<string, object> { ["newStatus"] = new[] { "Completed", "Aborted" } },
            },
            Targets = new IRuleTarget[] { new LambdaFunction(reportFn, new LambdaFunctionProps { DeadLetterQueue = eventsDlq, RetryAttempts = 4 }) },
        });

        // On-demand demo flights: the API invokes this when a supervisor presses "Demo flight".
        // No schedule, so it costs nothing while idle. It publishes like a real drone through IoT Core.
        var demoFn = NewFunction("DemoFlight", $"{fn}DemoFlightFunction::HandleAsync", "functions", 512, new()
        {
            ["DEMO_TICK_MS"] = "500",
        }, timeoutSeconds: 360);
        demoFn.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[] { "iot:Publish" },
            Resources = new[] { $"arn:{Partition}:iot:{Region}:{Account}:topic/gridsafety/drones/*" },
        }));
        demoFn.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps { Actions = new[] { "iot:DescribeEndpoint" }, Resources = new[] { "*" } }));
        // A retried flight would replay a mission that already landed: run each launch at most once.
        demoFn.ConfigureAsyncInvoke(new EventInvokeConfigOptions { RetryAttempts = 0 });
        apiFn.AddEnvironment("GridSafety__DemoFunctionName", demoFn.FunctionName);
        demoFn.GrantInvoke(apiFn);

        // ───────────── HTTP API ─────────────
        var httpApi = new HttpApi(this, "HttpApi", new HttpApiProps
        {
            ApiName = $"gridsafety-{props.Stage}",
            Description = "GridSafety ops API",
        });
        // Abuse guard: cap the whole API (via CloudFront or the raw execute-api URL) so a flood of
        // requests gets 429s from API Gateway instead of invoking Lambda and running up the bill.
        // Plenty for a handful of ops users; throttling is free, unlike WAF.
        var apiStage = (CfnStage)httpApi.DefaultStage!.Node.DefaultChild!;
        apiStage.DefaultRouteSettings = new CfnStage.RouteSettingsProperty { ThrottlingRateLimit = ApiRateLimitPerSecond, ThrottlingBurstLimit = ApiBurstLimit };
        var integration = new HttpLambdaIntegration("ApiIntegration", apiFn);

        // ───────────── CDN / SPA hosting ─────────────
        var spaRewrite = new CfFunction(this, "SpaRewrite", new Amazon.CDK.AWS.CloudFront.FunctionProps
        {
            Runtime = FunctionRuntime.JS_2_0,
            Comment = "SPA fallback to index.html",
            Code = FunctionCode.FromInline(
                """
                function handler(event) {
                  var req = event.request;
                  if (!req.uri.startsWith('/api/') && req.uri.indexOf('.') === -1) {
                    req.uri = '/index.html';
                  }
                  return req;
                }
                """),
        });
        var customDomain = props.DomainName is { Length: > 0 } dn ? dn : null;
        if (customDomain is not null && props.CertificateArn is not { Length: > 0 })
            throw new System.ArgumentException("domainName needs certificateArn: an ACM certificate for it in us-east-1.");
        var distribution = new Distribution(this, "Cdn", new DistributionProps
        {
            Comment = $"GridSafety {props.Stage}",
            DomainNames = customDomain is null ? null : new[] { customDomain },
            Certificate = customDomain is null ? null : Certificate.FromCertificateArn(this, "SiteCertificate", props.CertificateArn!),
            DefaultRootObject = "index.html",
            DefaultBehavior = new BehaviorOptions
            {
                Origin = S3BucketOrigin.WithOriginAccessControl(site),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                CachePolicy = CachePolicy.CACHING_OPTIMIZED,
                ResponseHeadersPolicy = ResponseHeadersPolicy.SECURITY_HEADERS,
                FunctionAssociations = new IFunctionAssociation[]
                {
                    new FunctionAssociation { Function = spaRewrite, EventType = FunctionEventType.VIEWER_REQUEST },
                },
            },
            AdditionalBehaviors = new Dictionary<string, IBehaviorOptions>
            {
                ["/api/*"] = new BehaviorOptions
                {
                    Origin = new HttpOrigin($"{httpApi.ApiId}.execute-api.{Region}.{UrlSuffix}"),
                    ViewerProtocolPolicy = ViewerProtocolPolicy.HTTPS_ONLY,
                    AllowedMethods = AllowedMethods.ALLOW_ALL,
                    CachePolicy = CachePolicy.CACHING_DISABLED,
                    OriginRequestPolicy = OriginRequestPolicy.ALL_VIEWER_EXCEPT_HOST_HEADER,
                },
            },
        });
        var cdnUrl = $"https://{distribution.DistributionDomainName}";
        var siteUrl = customDomain is null ? cdnUrl : $"https://{customDomain}";

        var client = userPool.AddClient("WebClient", new UserPoolClientOptions
        {
            GenerateSecret = false,
            AuthFlows = new AuthFlow { UserSrp = true },
            PreventUserExistenceErrors = true,
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows { AuthorizationCodeGrant = true },
                Scopes = new[] { OAuthScope.OPENID, OAuthScope.EMAIL, OAuthScope.PROFILE },
                // Both the custom domain and the CloudFront name keep working for sign-in.
                CallbackUrls = new[] { $"{siteUrl}/", $"{cdnUrl}/", "http://localhost:5173/" }.Distinct().ToArray(),
                LogoutUrls = new[] { $"{siteUrl}/", $"{cdnUrl}/", "http://localhost:5173/" }.Distinct().ToArray(),
            },
            IdTokenValidity = Duration.Hours(1),
            AccessTokenValidity = Duration.Hours(1),
            RefreshTokenValidity = Duration.Days(7),
        });

        var authorizer = new HttpJwtAuthorizer("CognitoAuthorizer",
            $"https://cognito-idp.{Region}.amazonaws.com/{userPool.UserPoolId}",
            new HttpJwtAuthorizerProps { JwtAudience = new[] { client.UserPoolClientId } });

        httpApi.AddRoutes(new AddRoutesOptions
        {
            Path = "/api/{proxy+}",
            Methods = new[] { HttpMethod.ANY },
            Integration = integration,
            Authorizer = authorizer,
        });
        httpApi.AddRoutes(new AddRoutesOptions
        {
            Path = "/api/health",
            Methods = new[] { HttpMethod.GET },
            Integration = integration,
            Authorizer = new HttpNoneAuthorizer(),
        });
        // Signed-out visitors get a read-only view. GET only: any other method on /api/public/* falls through to
        // the JWT-protected catch-all, and the API maps nothing but GETs there anyway.
        httpApi.AddRoutes(new AddRoutesOptions
        {
            Path = "/api/public/{proxy+}",
            Methods = new[] { HttpMethod.GET },
            Integration = integration,
            Authorizer = new HttpNoneAuthorizer(),
        });

        // Web app caching: Vite fingerprints everything under assets/ (index-<hash>.js), so those files can be
        // cached for a year. index.html, config.json and the icons keep their names, so browsers must revalidate
        // them on every load; otherwise a cached index.html keeps pointing at last deploy's bundle.
        var webDist = Path.Combine(repoRoot, "web", "dist");
        var assets = new BucketDeployment(this, "DeploySiteAssets", new BucketDeploymentProps
        {
            DestinationBucket = site,
            Sources = new[] { Source.Asset(webDist) },
            Exclude = new[] { "*" },
            Include = new[] { "assets/*" },
            CacheControl = new[] { CacheControl.FromString("public, max-age=31536000, immutable") },
            Prune = false, // keep the previous bundle so tabs opened before a deploy can still lazy-load it
            MemoryLimit = 512,
        });

        var pages = new BucketDeployment(this, "DeploySite", new BucketDeploymentProps
        {
            DestinationBucket = site,
            Exclude = new[] { "assets/*" }, // also keeps the sync's delete step away from the bundles
            CacheControl = new[] { CacheControl.FromString("no-cache") },
            Sources = new[]
            {
                Source.Asset(webDist),
                Source.JsonData("config.json", new Dictionary<string, object>
                {
                    ["apiBaseUrl"] = "/api",
                    ["authority"] = $"https://cognito-idp.{Region}.amazonaws.com/{userPool.UserPoolId}",
                    ["clientId"] = client.UserPoolClientId,
                    ["cognitoDomain"] = domain.BaseUrl(),
                    ["redirectUri"] = $"{siteUrl}/",
                }),
            },
            Distribution = distribution,
            DistributionPaths = new[] { "/*" },
            MemoryLimit = 512,
        });
        pages.Node.AddDependency(assets); // never publish an index.html before the bundle it references

        // ───────────── Observability ─────────────
        var alarmAction = new SnsAction(alarmTopic);
        void Alarm(string name, Metric metric, double threshold, string description)
        {
            var alarm = metric.CreateAlarm(this, name, new CreateAlarmOptions
            {
                Threshold = threshold,
                EvaluationPeriods = 1,
                ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
                TreatMissingData = TreatMissingData.NOT_BREACHING,
                AlarmDescription = description,
            });
            alarm.AddAlarmAction(alarmAction);
        }

        var functions = new[] { apiFn, ingestFn, streamFn, slaFn, escalationFn, notifierFn, reportFn, demoFn };
        foreach (var f in functions)
        {
            Alarm($"{f.Node.Id}Errors", f.MetricErrors(new MetricOptions { Period = Duration.Minutes(5) }), 1, $"{f.Node.Id} threw errors");
        }
        Alarm("Api5xx", httpApi.MetricServerError(new MetricOptions { Period = Duration.Minutes(5) }), 5, "API returned 5xx responses");
        Alarm("StreamIteratorAge", streamFn.Metric("IteratorAge", new MetricOptions { Statistic = "Maximum", Period = Duration.Minutes(5) }), 60_000,
            "Outbox publisher is falling behind the table stream (>60s)");
        foreach (var q in new[] { streamDlq, eventsDlq, pageDlq })
        {
            Alarm($"{q.Node.Id}Depth", q.MetricApproximateNumberOfMessagesVisible(new MetricOptions { Period = Duration.Minutes(5) }), 1,
                $"Messages landed in {q.Node.Id}; inspect and redrive");
        }

        var dashboard = new Dashboard(this, "Dashboard", new DashboardProps { DashboardName = $"GridSafety-{props.Stage}" });
        dashboard.AddWidgets(
            new GraphWidget(new GraphWidgetProps
            {
                Title = "Fleet: telemetry points / findings / drone alerts",
                Left = new[] { BizMetric("TelemetryPoints") },
                Right = new[] { BizMetric("FindingsReported"), BizMetric("DroneAlerts"), BizMetric("MessagesRejected") },
                Width = 12,
            }),
            // Pages, SLA scheduling and reports come from free AWS metrics; only escalations are custom.
            new GraphWidget(new GraphWidgetProps
            {
                Title = "Ops: pages / SLA timers / escalations / reports",
                Left = new IMetric[]
                {
                    opsTopic.MetricNumberOfMessagesPublished(new MetricOptions { Label = "Pages sent (incl. escalations)", Period = Duration.Minutes(5) }),
                    slaFn.MetricInvocations(new MetricOptions { Label = "SLA scheduler runs", Period = Duration.Minutes(5) }),
                    BizMetric("SlaEscalations"),
                    reportFn.MetricInvocations(new MetricOptions { Label = "Reports generated", Period = Duration.Minutes(5) }),
                },
                Width = 12,
            }));
        dashboard.AddWidgets(
            new GraphWidget(new GraphWidgetProps
            {
                Title = "API requests / 5xx / latency p95",
                Left = new IMetric[] { httpApi.MetricCount(), httpApi.MetricServerError() },
                Right = new IMetric[] { httpApi.MetricLatency(new MetricOptions { Statistic = "p95" }) },
                Width = 8,
            }),
            new GraphWidget(new GraphWidgetProps
            {
                Title = "Lambda errors",
                Left = functions.Select(f => (IMetric)f.MetricErrors()).ToArray(),
                Width = 8,
            }),
            new GraphWidget(new GraphWidgetProps
            {
                Title = "Dead-letter queues",
                Left = new IMetric[]
                {
                    streamDlq.MetricApproximateNumberOfMessagesVisible(),
                    eventsDlq.MetricApproximateNumberOfMessagesVisible(),
                    pageDlq.MetricApproximateNumberOfMessagesVisible(),
                },
                Width = 8,
            }));

        // ───────────── Outputs ─────────────
        new CfnOutput(this, "SiteUrl", new CfnOutputProps { Value = siteUrl });
        new CfnOutput(this, "CloudFrontDomain", new CfnOutputProps { Value = distribution.DistributionDomainName, Description = "CNAME target for a custom domain" });
        new CfnOutput(this, "ApiUrl", new CfnOutputProps { Value = $"{siteUrl}/api" });
        new CfnOutput(this, "UserPoolId", new CfnOutputProps { Value = userPool.UserPoolId });
        new CfnOutput(this, "UserPoolClientId", new CfnOutputProps { Value = client.UserPoolClientId });
        new CfnOutput(this, "TableName", new CfnOutputProps { Value = table.TableName });
        new CfnOutput(this, "EventBusName", new CfnOutputProps { Value = bus.EventBusName });
        new CfnOutput(this, "OpsTopicArn", new CfnOutputProps { Value = opsTopic.TopicArn });
        new CfnOutput(this, "ReportsBucket", new CfnOutputProps { Value = reports.BucketName });
        new CfnOutput(this, "SimulatorPolicyArn", new CfnOutputProps
        {
            Value = simulatorPolicy.ManagedPolicyArn,
            Description = "Attach to the IAM user/role that runs tools/GridSafety.Simulator --aws",
        });
        new CfnOutput(this, "DashboardUrl", new CfnOutputProps
        {
            Value = $"https://{Region}.console.aws.amazon.com/cloudwatch/home?region={Region}#dashboards:name={dashboard.DashboardName}",
        });
    }

    /// <summary>
    /// Custom metrics cost $0.30 per series per month after the first 10, so only business numbers AWS
    /// doesn't already track are custom (5 series: TelemetryPoints, FindingsReported, DroneAlerts,
    /// MessagesRejected, SlaEscalations; ColdStart capture is off). Powertools tags each with a
    /// "service" dimension; SEARCH sums across services.
    /// </summary>
    private static IMetric BizMetric(string name) => new MathExpression(new MathExpressionProps
    {
        Expression = $"SUM(SEARCH('{{{Namespace},service}} MetricName=\"{name}\"', 'Sum', 300))",
        Label = name,
        Period = Duration.Minutes(5),
        UsingMetrics = new Dictionary<string, IMetric>(),
    });
}
