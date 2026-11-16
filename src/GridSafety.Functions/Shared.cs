using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using AWS.Lambda.Powertools.Logging;
using GridSafety.Core;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace GridSafety.Functions;

internal static class Env
{
    public static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Missing environment variable {name}");

    public static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

/// <summary>Publishes domain events to the GridSafety EventBridge bus (max 10 entries per call).</summary>
public sealed class EventBridgePublisher(IAmazonEventBridge eventBridge, string busName) : IEventPublisher
{
    public async Task PublishAsync(IReadOnlyList<GridEvent> events, CancellationToken ct = default)
    {
        foreach (var batch in events.Chunk(10))
        {
            var res = await eventBridge.PutEventsAsync(new PutEventsRequest
            {
                Entries = batch.Select(e => new PutEventsRequestEntry
                {
                    EventBusName = busName,
                    Source = EventNames.Source,
                    DetailType = e.DetailType,
                    Detail = JsonSerializer.Serialize(e, Json.Options),
                    Resources = [$"{e.EntityType.ToLowerInvariant()}/{e.EntityId}"],
                }).ToList(),
            }, ct);
            if (res.FailedEntryCount > 0)
            {
                var reason = res.Entries?.FirstOrDefault(x => x.ErrorCode is not null)?.ErrorMessage;
                throw new InvalidOperationException($"EventBridge rejected {res.FailedEntryCount} event(s): {reason}");
            }
        }
    }
}

/// <summary>Ops notifications (on-call email/SMS/chat via SNS subscriptions).</summary>
public interface IOpsNotifier
{
    Task NotifyAsync(string subject, string message, string severity, CancellationToken ct = default);
}

public sealed class SnsOpsNotifier(IAmazonSimpleNotificationService sns, string topicArn) : IOpsNotifier
{
    public Task NotifyAsync(string subject, string message, string severity, CancellationToken ct = default) =>
        sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Subject = subject.Length > 100 ? subject[..100] : subject,
            Message = message,
            // Lets subscribers filter, e.g. SMS only for critical pages.
            MessageAttributes = new() { ["severity"] = new MessageAttributeValue { DataType = "String", StringValue = severity } },
        }, ct);

    public static IOpsNotifier FromEnvironment() =>
        new SnsOpsNotifier(new AmazonSimpleNotificationServiceClient(), Env.Required("OPS_TOPIC_ARN"));
}

/// <summary>
/// At-least-once delivery (SQS, EventBridge, Scheduler) means handlers can see the same event twice.
/// Each side effect claims its event id with a conditional write, so nobody gets paged twice.
/// Records expire through the table's TTL attribute.
/// </summary>
public sealed class IdempotencyGuard(IAmazonDynamoDB db, string tableName)
{
    public async Task<bool> TryClaimAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = new()
                {
                    ["PK"] = new AttributeValue { S = $"IDEMPOTENCY#{key}" },
                    ["SK"] = new AttributeValue { S = "IDEMPOTENCY" },
                    ["EntityType"] = new AttributeValue { S = "Idempotency" },
                    ["ExpiresAt"] = new AttributeValue { N = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
                },
                ConditionExpression = "attribute_not_exists(PK)",
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public Task ReleaseAsync(string key, CancellationToken ct = default) =>
        db.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = new()
            {
                ["PK"] = new AttributeValue { S = $"IDEMPOTENCY#{key}" },
                ["SK"] = new AttributeValue { S = "IDEMPOTENCY" },
            },
        }, ct);
}

internal static class Repo
{
    public static DynamoGridRepository FromEnvironment() => new(new AmazonDynamoDBClient(), Env.Required("TABLE_NAME"));
}

