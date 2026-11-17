using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using GridSafety.Api;
using GridSafety.Core;
using GridSafety.Functions;

namespace GridSafety.Tests;

public class AlertSettingsTests
{
    private static GridEvent Finding(int sev) => new()
    {
        DetailType = EventNames.FindingCreated, EntityType = "Finding", EntityId = "f1", NewStatus = "New",
        Severity = sev, TriageDueAt = DateTimeOffset.UtcNow.AddHours(4), Summary = "Split crossarm", OccurredAt = DateTimeOffset.UtcNow,
    };

    private static GridEvent MissionEnded(string status) => new()
    {
        DetailType = EventNames.MissionStatusChanged, EntityType = "Mission", EntityId = "m1", MissionId = "m1",
        PreviousStatus = "InFlight", NewStatus = status, Summary = "Pittsburg 115 kV patrol", OccurredAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Defaults_match_what_was_paged_before_settings_existed()
    {
        var s = AlertSettings.Default();
        Assert.True(s.Allows(AlertType.CriticalFinding, 4));
        Assert.False(s.Allows(AlertType.CriticalFinding, 3));
        Assert.True(s.Allows(AlertType.OverdueTriage, 5));
        Assert.True(s.Allows(AlertType.FlightAlert));
        Assert.False(s.Allows(AlertType.MissionComplete));
        Assert.Empty(s.Recipients);
    }

    [Fact]
    public void Threshold_and_switches_decide_what_emails()
    {
        var s = new AlertSettings { MinSeverity = 2, MissionComplete = true, FlightAlerts = false };
        Assert.NotNull(AlertNotifier.Compose(Finding(2), s));
        Assert.Null(AlertNotifier.Compose(Finding(1), s));

        var ended = AlertNotifier.Compose(MissionEnded("Completed"), s)!.Value;
        Assert.Equal(AlertType.MissionComplete, ended.Type);
        Assert.Contains("Mission completed", ended.Subject);
        Assert.NotNull(AlertNotifier.Compose(MissionEnded("Aborted"), s));
        Assert.Null(AlertNotifier.Compose(MissionEnded("InFlight"), s));

        var alert = new GridEvent { DetailType = EventNames.DroneAlertRaised, EntityType = "Drone", EntityId = "D", Level = "Critical", Summary = "x" };
        Assert.Null(AlertNotifier.Compose(alert, s));

        s.CriticalFindings = false;
        Assert.Null(AlertNotifier.Compose(Finding(5), s));
    }

    [Fact]
    public void Recipients_are_normalized_and_validated()
    {
        Assert.Equal(["ops@example.com", "b@example.org"], AlertSettings.NormalizeRecipients([" Ops@Example.com ", "", null, "ops@example.com", "b@example.org"]));

        Assert.Empty(AlertSettings.Validate(new AlertSettings { Recipients = ["ops@example.com"] }));
        Assert.Contains("recipients", AlertSettings.Validate(new AlertSettings { Recipients = ["not-an-email"] }).Keys);
        Assert.Contains("recipients", AlertSettings.Validate(new AlertSettings { Recipients = ["a@b.com, c@d.com"] }).Keys);
        Assert.Contains("recipients", AlertSettings.Validate(new AlertSettings { Recipients = [.. Enumerable.Range(0, 11).Select(i => $"u{i}@example.com")] }).Keys);
        Assert.Contains("minSeverity", AlertSettings.Validate(new AlertSettings { MinSeverity = 0 }).Keys);
        Assert.Contains("minSeverity", AlertSettings.Validate(new AlertSettings { MinSeverity = 6 }).Keys);
    }

    [Fact]
    public async Task Store_rejects_a_stale_save()
    {
        var store = new InMemoryAlertSettingsStore();
        var a = await store.GetAsync();
        var b = await store.GetAsync();
        a.MinSeverity = 3;
        await store.SaveAsync(a);
        Assert.Equal(1, a.Version);
        await Assert.ThrowsAsync<ConcurrencyException>(() => store.SaveAsync(b));
        Assert.Equal(3, (await store.GetAsync()).MinSeverity);
    }

    [Fact]
    public async Task Overdue_finding_is_flagged_but_not_emailed_when_that_alert_is_off()
    {
        var repo = new InMemoryGridRepository();
        var f = WorkflowTests.NewFinding(5, due: DateTimeOffset.UtcNow.AddMinutes(-5));
        await repo.SaveFindingAsync(f);
        var store = new InMemoryAlertSettingsStore();
        var s = await store.GetAsync();
        s.OverdueTriage = false;
        await store.SaveAsync(s);
        var notifier = new FakeNotifier();

        await new SlaEscalation(repo, notifier, TimeProvider.System, store)
            .HandleAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new EscalationPayload(f.Id), Json.Options)), new TestLambdaContext());

        Assert.Empty(notifier.Sent);
        Assert.True((await repo.GetFindingAsync(f.Id))!.Escalated);
    }

    [Fact]
    public async Task Overdue_finding_below_the_threshold_is_not_emailed()
    {
        var repo = new InMemoryGridRepository();
        var f = WorkflowTests.NewFinding(3, due: DateTimeOffset.UtcNow.AddMinutes(-5));
        await repo.SaveFindingAsync(f);
        var notifier = new FakeNotifier();

        await new SlaEscalation(repo, notifier, TimeProvider.System, new InMemoryAlertSettingsStore())
            .HandleAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new EscalationPayload(f.Id), Json.Options)), new TestLambdaContext());

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Sns_sync_subscribes_new_addresses_and_drops_removed_confirmed_ones()
    {
        var sns = FakeSns.Create(
            new Subscription { Protocol = "email", Endpoint = "keep@example.com", SubscriptionArn = "arn:keep" },
            new Subscription { Protocol = "email", Endpoint = "gone@example.com", SubscriptionArn = "arn:gone" },
            new Subscription { Protocol = "email", Endpoint = "waiting@example.com", SubscriptionArn = "PendingConfirmation" });
        var recipients = new SnsAlertRecipients((IAmazonSimpleNotificationService)sns, "arn:topic");

        await recipients.SyncAsync(["keep@example.com", "new@example.com"]);

        Assert.Equal(["new@example.com"], sns.Subscribed);
        Assert.Equal(["arn:gone"], sns.Unsubscribed); // a pending one can't be removed, so it is left alone

        var status = await recipients.StatusAsync(["KEEP@example.com", "waiting@example.com", "other@example.com"]);
        Assert.Equal(["Confirmed", "Pending", "NotSubscribed"], status.Select(r => r.Status));
    }

    /// <summary>Answers the three SNS calls the recipient sync makes; anything else throws.</summary>
    public class FakeSns : DispatchProxy
    {
        public List<Subscription> Existing { get; } = [];
        public List<string> Subscribed { get; } = [];
        public List<string> Unsubscribed { get; } = [];

        public static FakeSns Create(params Subscription[] existing)
        {
            var proxy = (FakeSns)(object)Create<IAmazonSimpleNotificationService, FakeSns>();
            proxy.Existing.AddRange(existing);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IAmazonSimpleNotificationService.ListSubscriptionsByTopicAsync) when args![0] is ListSubscriptionsByTopicRequest =>
                Task.FromResult(new ListSubscriptionsByTopicResponse { Subscriptions = [.. Existing] }),
            nameof(IAmazonSimpleNotificationService.SubscribeAsync) when args![0] is SubscribeRequest r =>
                Record(Subscribed, r.Endpoint, new SubscribeResponse()),
            nameof(IAmazonSimpleNotificationService.UnsubscribeAsync) when args![0] is UnsubscribeRequest r =>
                Record(Unsubscribed, r.SubscriptionArn, new UnsubscribeResponse()),
            _ => throw new NotSupportedException(method?.Name),
        };

        private static Task<T> Record<T>(List<string> log, string value, T response)
        {
            log.Add(value);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Notifier_reads_settings_and_skips_disabled_alerts()
    {
        var store = new InMemoryAlertSettingsStore();
        var s = await store.GetAsync();
        s.MissionComplete = true;
        s.MinSeverity = 5;
        await store.SaveAsync(s);
        var notifier = new FakeNotifier();
        var fn = new AlertNotifier(notifier, new IdempotencyGuard(null!, "unused"), store);

        // Below the threshold: dropped before the idempotency claim (which would need DynamoDB).
        var res = await fn.HandleAsync(Sqs(Finding(4)), new TestLambdaContext());
        Assert.Empty(res.BatchItemFailures);
        Assert.Empty(notifier.Sent);
    }

    private static SQSEvent Sqs(GridEvent e) => new()
    {
        Records =
        [
            new SQSEvent.SQSMessage
            {
                MessageId = "1",
                Body = JsonSerializer.Serialize(new { id = "evt-1", detail = e }, Json.Options),
            },
        ],
    };
}

/// <summary>The settings API: anyone signed in reads, only supervisors write, guests get nothing.</summary>
public class AlertSettingsApiTests(ApiTests.Factory factory) : IClassFixture<ApiTests.Factory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Client(string user, bool supervisor = false)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", user);
        if (supervisor) c.DefaultRequestHeaders.Add("X-Dev-Role", "Supervisor");
        return c;
    }

    [Fact]
    public async Task Supervisors_configure_alerts_and_everyone_else_only_reads()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/alert-settings")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/public/alert-settings")).StatusCode);

        var inspector = Client("alex");
        var view = (await inspector.GetFromJsonAsync<AlertSettingsView>("/api/alert-settings", JsonOpts))!;
        Assert.False(view.EmailEnabled); // local: nothing is emailed
        var settings = view.Settings;
        settings.MissionComplete = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PutAsJsonAsync("/api/alert-settings", settings, JsonOpts)).StatusCode);

        var sup = Client("sam", supervisor: true);
        settings.Recipients = ["not-an-email"];
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync("/api/alert-settings", settings, JsonOpts)).StatusCode);

        settings.Recipients = [" Lead@Example.com ", "lead@example.com", "crew@example.org"];
        settings.MinSeverity = 3;
        var res = await sup.PutAsJsonAsync("/api/alert-settings", settings, JsonOpts);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var saved = (await res.Content.ReadFromJsonAsync<AlertSettingsView>(JsonOpts))!;
        Assert.Equal(["lead@example.com", "crew@example.org"], saved.Settings.Recipients);
        Assert.Equal(["Local", "Local"], saved.Recipients.Select(r => r.Status));
        Assert.Equal(3, saved.Settings.MinSeverity);
        Assert.True(saved.Settings.MissionComplete);
        Assert.Equal("sam@gridsafety.dev", saved.Settings.UpdatedBy);

        // The first copy is now stale.
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PutAsJsonAsync("/api/alert-settings", settings, JsonOpts)).StatusCode);

        var reread = (await inspector.GetFromJsonAsync<AlertSettingsView>("/api/alert-settings", JsonOpts))!;
        Assert.Equal(saved.Settings.Version, reread.Settings.Version);
        Assert.Equal(2, reread.Settings.Recipients.Count);
    }
}
