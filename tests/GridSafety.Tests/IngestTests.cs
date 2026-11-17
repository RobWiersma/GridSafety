using System.Text.Json;
using Amazon.Lambda.TestUtilities;
using GridSafety.Core;
using GridSafety.Functions;

namespace GridSafety.Tests;

public class DroneIngestTests
{
    private static readonly Circuit Circuit = TestCircuits.Line;

    private sealed class Fixture
    {
        public InMemoryGridRepository Repo { get; } = new();
        public InMemoryEventPublisher Events { get; } = new();
        public DroneIngestService Service { get; }
        public Mission Mission { get; }

        public Fixture()
        {
            Service = new DroneIngestService(Repo, TestCircuits.Catalog, Events, TimeProvider.System);
            Mission = Mission.Plan(Circuit, "DRN-1", "Pat", null, DateTimeOffset.UtcNow);
            Repo.SaveMissionAsync(Mission).GetAwaiter().GetResult();
        }

        public Task<IngestResult> Telemetry(params TelemetryPoint[] points) =>
            Service.HandleAsync(new DroneMessage { Kind = MessageKinds.Telemetry, DroneId = "DRN-1", MissionId = Mission.Id, Points = [.. points] });
    }

    private static TelemetryPoint P(int structureIndex, int secondsFromNow, double alt = 60, double batt = 80, double northOffset = 0)
    {
        var loc = Geo.Offset(Circuit.Structures[structureIndex].Location, northOffset, 0);
        return new TelemetryPoint(DateTimeOffset.UtcNow.AddSeconds(secondsFromNow), loc.Lat, loc.Lon, alt, batt, 14);
    }

    [Fact]
    public async Task First_telemetry_launches_mission_and_tracks_progress()
    {
        var fx = new Fixture();
        await fx.Telemetry(P(0, 0), P(1, 20), P(2, 40));

        var m = (await fx.Repo.GetMissionAsync(fx.Mission.Id))!;
        Assert.Equal(MissionStatus.InFlight, m.Status);
        Assert.Equal(3, m.StructuresInspected.Count);
        Assert.InRange(m.DistanceFlownMeters, 300, 700);
        Assert.Equal(3, (await fx.Repo.GetTrackAsync(fx.Mission.Id)).Count);

        var drone = (await fx.Repo.GetDroneAsync("DRN-1"))!;
        Assert.Equal(DroneStatus.Flying, drone.Status);
        Assert.Equal(fx.Mission.Id, drone.MissionId);
    }

    [Fact]
    public async Task Alerts_publish_once_per_onset()
    {
        var fx = new Fixture();
        await fx.Telemetry(P(0, 0));
        var r1 = await fx.Telemetry(P(1, 2, alt: 130));
        var r2 = await fx.Telemetry(P(2, 4, alt: 131));        // still high: no new page
        var r3 = await fx.Telemetry(P(3, 6, alt: 60));
        var r4 = await fx.Telemetry(P(4, 8, alt: 129));         // climbed again: new page

        Assert.Single(r1.AlertsRaised);
        Assert.Empty(r2.AlertsRaised);
        Assert.Empty(r3.AlertsRaised);
        Assert.Single(r4.AlertsRaised);
        Assert.Equal(2, fx.Events.Published.Count(e => e.DetailType == EventNames.DroneAlertRaised));
        Assert.Equal(2, (await fx.Repo.GetMissionAsync(fx.Mission.Id))!.RecentAlerts.Count);
    }

    [Fact]
    public async Task Finding_report_creates_work_order_with_sla()
    {
        var fx = new Fixture();
        var result = await fx.Service.HandleAsync(new DroneMessage
        {
            Kind = MessageKinds.Finding, DroneId = "DRN-1", MissionId = fx.Mission.Id,
            Finding = new FindingReport(Circuit.Structures[4].Id, FindingType.VegetationEncroachment, 9, 1.4, "Oak limb"),
        });

        var f = (await fx.Repo.GetFindingAsync(result.FindingId!))!;
        Assert.Equal(5, f.Severity);              // clamped
        Assert.Equal(1, f.Confidence);            // clamped
        Assert.Equal(Circuit.Structures[4].Label, f.StructureLabel);
        Assert.NotNull(f.TriageDueAt);
        Assert.Equal(1, (await fx.Repo.GetMissionAsync(fx.Mission.Id))!.FindingCount);
    }

    [Fact]
    public async Task Landing_completes_mission_and_idles_drone()
    {
        var fx = new Fixture();
        await fx.Telemetry(P(0, 0));
        await fx.Service.HandleAsync(new DroneMessage { Kind = MessageKinds.Landed, DroneId = "DRN-1", MissionId = fx.Mission.Id });

        Assert.Equal(MissionStatus.Completed, (await fx.Repo.GetMissionAsync(fx.Mission.Id))!.Status);
        Assert.Equal(DroneStatus.Idle, (await fx.Repo.GetDroneAsync("DRN-1"))!.Status);
    }

    [Fact]
    public async Task Rejects_drone_not_assigned_to_mission()
    {
        var fx = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Service.HandleAsync(
            new DroneMessage { Kind = MessageKinds.Telemetry, DroneId = "IMPOSTOR", MissionId = fx.Mission.Id, Points = [P(0, 0)] }));
    }

    [Fact]
    public async Task Out_of_order_telemetry_does_not_move_drone_backwards()
    {
        var fx = new Fixture();
        await fx.Telemetry(P(5, 10));
        await fx.Telemetry(P(1, 0));             // late, older packet

        var drone = (await fx.Repo.GetDroneAsync("DRN-1"))!;
        Assert.InRange(Geo.DistanceMeters(drone.Position, Circuit.Structures[5].Location), 0, 1);
    }

    [Fact]
    public async Task Concurrent_messages_do_not_lose_updates()
    {
        var fx = new Fixture();
        await fx.Telemetry(P(0, 0));
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => fx.Service.HandleAsync(new DroneMessage
        {
            Kind = MessageKinds.Finding, DroneId = "DRN-1", MissionId = fx.Mission.Id,
            Finding = new FindingReport(Circuit.Structures[i].Id, FindingType.MissingHardware, 2, 0.8, null),
        })));
        Assert.Equal(6, (await fx.Repo.GetMissionAsync(fx.Mission.Id))!.FindingCount);
    }
}

public class StreamPublisherTests
{
    private static readonly Circuit Circuit = TestCircuits.Line;
    private static string Doc<T>(T x) => JsonSerializer.Serialize(x, Json.Options);

    [Fact]
    public void Mission_status_change_is_published_but_progress_updates_are_not()
    {
        var before = Mission.Plan(Circuit, "D", "P", null, DateTimeOffset.UtcNow);
        var after = Mission.Plan(Circuit, "D", "P", null, DateTimeOffset.UtcNow);
        after.MoveTo(MissionStatus.InFlight, DateTimeOffset.UtcNow);

        var e = StreamPublisher.ToEvent("MODIFY", "Mission", Doc(before), Doc(after))!;
        Assert.Equal(EventNames.MissionStatusChanged, e.DetailType);
        Assert.Equal("Planned", e.PreviousStatus);
        Assert.Equal("InFlight", e.NewStatus);

        var progressed = Doc(after);
        Assert.Null(StreamPublisher.ToEvent("MODIFY", "Mission", progressed, progressed));
    }

    [Fact]
    public void New_finding_carries_severity_and_deadline()
    {
        var f = WorkflowTests.NewFinding(5, due: DateTimeOffset.UtcNow.AddHours(4));
        var e = StreamPublisher.ToEvent("INSERT", "Finding", null, Doc(f))!;
        Assert.Equal(EventNames.FindingCreated, e.DetailType);
        Assert.Equal(5, e.Severity);
        Assert.Equal(f.TriageDueAt, e.TriageDueAt);
        Assert.Contains("thermal hotspot", e.Summary);
    }

    [Fact]
    public void Ignores_unknown_entities() =>
        Assert.Null(StreamPublisher.ToEvent("INSERT", "Telemetry", null, "{}"));
}

public class SlaAndNotifierTests
{
    private static GridEvent Created(int sev, DateTimeOffset? due) => new()
    {
        DetailType = EventNames.FindingCreated, EntityType = "Finding", EntityId = "f1", NewStatus = "New",
        Severity = sev, TriageDueAt = due, Summary = "Sev x", OccurredAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Schedules_only_new_findings_with_a_deadline()
    {
        Assert.True(SlaScheduler.ShouldSchedule(Created(5, DateTimeOffset.UtcNow.AddHours(4))));
        Assert.False(SlaScheduler.ShouldSchedule(Created(2, null)));
        Assert.False(SlaScheduler.ShouldSchedule(Created(5, DateTimeOffset.UtcNow) with { DetailType = EventNames.FindingStatusChanged }));
    }

    [Fact]
    public void Scheduler_expression_and_name_are_valid()
    {
        Assert.Equal("at(2026-11-22T09:30:00)", SlaScheduler.AtExpression(new DateTimeOffset(2026, 11, 22, 1, 30, 0, TimeSpan.FromHours(-8))));
        Assert.Matches("^[0-9a-zA-Z-_.]{1,64}$", SlaScheduler.ScheduleName(Ids.New()));
    }

    [Fact]
    public void Pages_for_critical_alerts_and_high_severity_findings_only()
    {
        var defaults = AlertSettings.Default();
        Assert.NotNull(AlertNotifier.Compose(Created(4, DateTimeOffset.UtcNow), defaults));
        Assert.Null(AlertNotifier.Compose(Created(3, DateTimeOffset.UtcNow), defaults));

        var alert = new GridEvent { DetailType = EventNames.DroneAlertRaised, EntityType = "Drone", EntityId = "D", Level = "Critical", NewStatus = "CorridorBreach", Summary = "x" };
        Assert.Equal("critical", AlertNotifier.Compose(alert, defaults)!.Value.Severity);
        Assert.Null(AlertNotifier.Compose(alert with { Level = "Warning" }, defaults));
    }

    [Fact]
    public async Task Escalation_pages_untriaged_finding_once()
    {
        var repo = new InMemoryGridRepository();
        var f = WorkflowTests.NewFinding(5, due: DateTimeOffset.UtcNow.AddMinutes(-5));
        await repo.SaveFindingAsync(f);
        var notifier = new FakeNotifier();
        var fn = new SlaEscalation(repo, notifier, TimeProvider.System, new InMemoryAlertSettingsStore());

        await fn.HandleAsync(Payload(f.Id), new TestLambdaContext());
        await fn.HandleAsync(Payload(f.Id), new TestLambdaContext());   // scheduler retry / duplicate

        Assert.Single(notifier.Sent);
        Assert.Contains("SLA breach", notifier.Sent[0].Subject);
        Assert.True((await repo.GetFindingAsync(f.Id))!.Escalated);
    }

    [Fact]
    public async Task Escalation_skips_triaged_finding()
    {
        var repo = new InMemoryGridRepository();
        var f = WorkflowTests.NewFinding(5, due: DateTimeOffset.UtcNow.AddMinutes(-5));
        f.MoveTo(FindingStatus.Triaged, DateTimeOffset.UtcNow, "sup");
        await repo.SaveFindingAsync(f);
        var notifier = new FakeNotifier();

        await new SlaEscalation(repo, notifier, TimeProvider.System, new InMemoryAlertSettingsStore()).HandleAsync(Payload(f.Id), new TestLambdaContext());
        Assert.Empty(notifier.Sent);
    }

    private static MemoryStream Payload(string id) => new(JsonSerializer.SerializeToUtf8Bytes(new EscalationPayload(id), Json.Options));
}

public class ReportTests
{
    [Fact]
    public void Renders_self_contained_html_and_escapes_input()
    {
        var c = StaticCircuitCatalog.Instance.All[1];
        var m = Mission.Plan(c, "DRN-9", "<script>alert(1)</script>", null, DateTimeOffset.UtcNow.AddMinutes(-30));
        m.MoveTo(MissionStatus.InFlight, DateTimeOffset.UtcNow.AddMinutes(-25));
        m.MoveTo(MissionStatus.Completed, DateTimeOffset.UtcNow);
        var track = c.Structures.Select((s, i) => new TelemetryPoint(DateTimeOffset.UtcNow.AddSeconds(i), s.Location.Lat, s.Location.Lon, 60, 80, 14)).ToList();

        var html = MissionReportGenerator.Render(m, c, track, [WorkflowTests.NewFinding(4)], DateTimeOffset.UtcNow);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<svg", html);
        Assert.Contains("<h2>Captures</h2>", html);
        Assert.Contains("<img src=\"data:image/svg+xml;base64,", html);
        Assert.Contains(c.Name, html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}

internal sealed class FakeNotifier : IOpsNotifier
{
    public List<(string Subject, string Body, string Severity, AlertType Type)> Sent { get; } = [];

    public Task NotifyAsync(string subject, string message, string severity, AlertType type, CancellationToken ct = default)
    {
        Sent.Add((subject, message, severity, type));
        return Task.CompletedTask;
    }
}
