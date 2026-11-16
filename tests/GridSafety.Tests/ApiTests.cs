using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSafety.Api;
using GridSafety.Core;
using GridSafety.Core.Simulation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GridSafety.Tests;

/// <summary>Full HTTP round-trips through the real pipeline with the in-memory store and dev auth.</summary>
public class ApiTests(ApiTests.Factory factory) : IClassFixture<ApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development").UseSetting("GridSafety:DemoTickMs", "0"); // demo flights finish instantly
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Client(string user = "alex", bool supervisor = false)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", user);
        if (supervisor) c.DefaultRequestHeaders.Add("X-Dev-Role", "Supervisor");
        return c;
    }

    private static async Task<MissionView> PlanAsync(HttpClient c, string drone)
    {
        var res = await c.PostAsJsonAsync("/api/missions", new PlanMissionRequest("PITT-MART-115", drone, "Pat"), JsonOpts);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<MissionView>(JsonOpts))!;
    }

    private static async Task<string> ReportFindingAsync(HttpClient c, MissionView m, int severity)
    {
        var circuit = StaticCircuitCatalog.Instance.Find(m.Mission.CircuitId)!;
        var res = await c.PostAsJsonAsync("/api/ingest", new DroneMessage
        {
            Kind = MessageKinds.Finding, DroneId = m.Mission.DroneId, MissionId = m.Mission.Id,
            Finding = new FindingReport(circuit.Structures[2].Id, FindingType.CrossarmDamage, severity, 0.9, "Split crossarm"),
        }, JsonOpts);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<IngestResult>(JsonOpts))!.FindingId!;
    }

    [Fact]
    public async Task Health_is_public_everything_else_needs_auth()
    {
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/missions")).StatusCode);
    }

    [Fact]
    public async Task Visits_are_counted_anonymously_and_only_supervisors_see_stats()
    {
        var guest = factory.CreateClient();
        guest.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/142.0 Safari/537.36");
        guest.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.23");
        var before = (await Client("sam", supervisor: true).GetFromJsonAsync<VisitorStatsView>("/api/visitors?days=1", JsonOpts))!;

        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync("/api/public/visit", new VisitRequest("ops", "https://www.linkedin.com/in/x", false), JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.PostAsJsonAsync("/api/public/visit", new VisitRequest("findings", null, false), JsonOpts)).StatusCode);

        // Bots are ignored.
        var bot = factory.CreateClient();
        bot.DefaultRequestHeaders.UserAgent.ParseAdd("Googlebot/2.1");
        Assert.Equal(HttpStatusCode.NoContent, (await bot.PostAsJsonAsync("/api/public/visit", new VisitRequest("ops", null, false), JsonOpts)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/visitors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("alex").GetAsync("/api/visitors")).StatusCode);

        var after = (await Client("sam", supervisor: true).GetFromJsonAsync<VisitorStatsView>("/api/visitors?days=1", JsonOpts))!;
        Assert.Equal(before.PageViews + 2, after.PageViews);
        Assert.Equal(before.VisitorDays + 1, after.VisitorDays);
        Assert.Contains(after.Referrers, r => r.Name == "linkedin.com");
        var raw = await (await Client("sam", supervisor: true).GetAsync("/api/visitors?days=1")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("198.51.100.23", raw);
    }

    [Fact]
    public async Task Guests_can_read_but_cannot_change_anything()
    {
        var guest = factory.CreateClient(); // no identity at all
        var me = await guest.GetFromJsonAsync<MeView>("/api/public/me", JsonOpts);
        Assert.True(me!.IsGuest);
        Assert.False(me.IsSupervisor);

        foreach (var path in new[] { "/api/public/circuits", "/api/public/stats", "/api/public/drones", "/api/public/missions", "/api/public/findings" })
            Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(path)).StatusCode);

        // Writes don't exist under /public, and the real write routes still need a signed-in user.
        var plan = new PlanMissionRequest("PITT-MART-115", "DRN-GUEST", "Pat");
        Assert.True((await guest.PostAsJsonAsync("/api/public/missions", plan, JsonOpts)).StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/api/missions", plan, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest(null), JsonOpts)).StatusCode);
    }

    [Fact]
    public async Task Guest_view_hides_who_did_what_and_offers_no_actions()
    {
        var sup = Client("pat.ops@example.com", supervisor: true);
        var m = await PlanAsync(sup, "DRN-GUEST-2");
        var findingId = await ReportFindingAsync(sup, m, 3);
        (await sup.PostAsJsonAsync($"/api/findings/{findingId}/status",
            new FindingStatusRequest(FindingStatus.Triaged, "Called pat.ops@example.com about it", null), JsonOpts)).EnsureSuccessStatusCode();

        var guest = factory.CreateClient();
        var mission = await guest.GetStringAsync($"/api/public/missions/{m.Mission.Id}");
        var finding = await guest.GetStringAsync($"/api/public/findings/{findingId}");

        foreach (var json in new[] { mission, finding })
        {
            Assert.DoesNotContain("pat.ops", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(GuestView.Redacted, json, StringComparison.Ordinal);
            Assert.Contains("\"nextStatuses\":[]", json, StringComparison.Ordinal);
        }
        Assert.Contains("Called Ops user about it", finding, StringComparison.Ordinal); // note kept, email scrubbed
        Assert.StartsWith("<svg", await guest.GetStringAsync($"/api/public/findings/{findingId}/image"), StringComparison.Ordinal);

        // Signed-in users still see the real names.
        Assert.Contains("pat.ops@example.com", await sup.GetStringAsync($"/api/missions/{m.Mission.Id}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Me_reflects_cognito_groups()
    {
        Assert.False((await Client().GetFromJsonAsync<MeView>("/api/me", JsonOpts))!.IsSupervisor);
        Assert.True((await Client(supervisor: true).GetFromJsonAsync<MeView>("/api/me", JsonOpts))!.IsSupervisor);
    }

    [Fact]
    public async Task Lists_circuits()
    {
        var circuits = await Client().GetFromJsonAsync<List<Circuit>>("/api/circuits", JsonOpts);
        Assert.Equal(StaticCircuitCatalog.Instance.All.Count, circuits!.Count);
        Assert.All(circuits, c => Assert.True(c.Structures.Count >= 3));
    }

    [Fact]
    public async Task Plans_mission_and_prevents_double_booking_a_drone()
    {
        var c = Client();
        var m = await PlanAsync(c, "DRN-API-1");
        Assert.Equal(MissionStatus.Planned, m.Mission.Status);
        Assert.Contains(MissionStatus.Aborted, m.NextStatuses);

        var again = await c.PostAsJsonAsync("/api/missions", new PlanMissionRequest("MART-60-A", "DRN-API-1", "Pat"), JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Validates_mission_input()
    {
        var res = await Client().PostAsJsonAsync("/api/missions", new PlanMissionRequest("NOPE", "bad id!", ""), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("circuitId", body);
        Assert.Contains("droneId", body);
        Assert.Contains("pilot", body);
    }

    [Fact]
    public async Task Telemetry_flows_through_to_track_drones_and_stats()
    {
        var c = Client();
        var m = await PlanAsync(c, "DRN-API-2");
        var circuit = StaticCircuitCatalog.Instance.Find(m.Mission.CircuitId)!;
        var points = circuit.Structures.Take(4).Select((s, i) => new TelemetryPoint(DateTimeOffset.UtcNow.AddSeconds(i), s.Location.Lat, s.Location.Lon, 60, 90 - i, 14)).ToList();

        (await c.PostAsJsonAsync("/api/ingest", new DroneMessage { Kind = MessageKinds.Telemetry, DroneId = "DRN-API-2", MissionId = m.Mission.Id, Points = points }, JsonOpts))
            .EnsureSuccessStatusCode();

        var view = await c.GetFromJsonAsync<MissionView>($"/api/missions/{m.Mission.Id}", JsonOpts);
        Assert.Equal(MissionStatus.InFlight, view!.Mission.Status);
        Assert.True(view.Coverage > 0);
        Assert.Equal(4, (await c.GetFromJsonAsync<List<TelemetryPoint>>($"/api/missions/{m.Mission.Id}/track", JsonOpts))!.Count);
        Assert.Contains((await c.GetFromJsonAsync<List<DroneState>>("/api/drones", JsonOpts))!, d => d.DroneId == "DRN-API-2" && d.Status == DroneStatus.Flying);
        Assert.True((await c.GetFromJsonAsync<OpsStats>("/api/stats", JsonOpts))!.DronesFlying >= 1);
    }

    [Fact]
    public async Task Flight_alerts_can_be_acknowledged_by_signed_in_users_only()
    {
        var c = Client("alex");
        var m = await PlanAsync(c, "DRN-ACK");
        var circuit = StaticCircuitCatalog.Instance.Find(m.Mission.CircuitId)!;
        var t0 = DateTimeOffset.UtcNow;
        // Low battery, then a climb above the ceiling: two alerts.
        var points = new[]
        {
            new TelemetryPoint(t0, circuit.Structures[0].Location.Lat, circuit.Structures[0].Location.Lon, 60, 25, 14),
            new TelemetryPoint(t0.AddSeconds(2), circuit.Structures[1].Location.Lat, circuit.Structures[1].Location.Lon, 130, 24, 14),
        };
        (await c.PostAsJsonAsync("/api/ingest", new DroneMessage { Kind = MessageKinds.Telemetry, DroneId = "DRN-ACK", MissionId = m.Mission.Id, Points = [.. points] }, JsonOpts))
            .EnsureSuccessStatusCode();
        var alerts = (await c.GetFromJsonAsync<MissionView>($"/api/missions/{m.Mission.Id}", JsonOpts))!.Mission.RecentAlerts;
        Assert.Equal(2, alerts.Count);

        // Guests can't acknowledge.
        var guest = factory.CreateClient();
        Assert.True((await guest.PostAsJsonAsync($"/api/public/missions/{m.Mission.Id}/alerts/ack", new AlertAckRequest(null, null), JsonOpts)).StatusCode
            is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostAsJsonAsync($"/api/missions/{m.Mission.Id}/alerts/ack", new AlertAckRequest(null, null), JsonOpts)).StatusCode);

        // One alert by time and kind…
        var altitude = alerts.Single(a => a.Kind == AlertKind.AltitudeCeiling);
        var res = await c.PostAsJsonAsync($"/api/missions/{m.Mission.Id}/alerts/ack", new AlertAckRequest(altitude.At, altitude.Kind), JsonOpts);
        var after = (await res.Content.ReadFromJsonAsync<MissionView>(JsonOpts))!.Mission.RecentAlerts;
        Assert.Equal("alex@gridsafety.dev", after.Single(a => a.Kind == AlertKind.AltitudeCeiling).AcknowledgedBy);
        Assert.Null(after.Single(a => a.Kind == AlertKind.LowBattery).AcknowledgedAt);

        // …then the rest at once. Guests see that it was acknowledged, but not by whom.
        (await c.PostAsJsonAsync($"/api/missions/{m.Mission.Id}/alerts/ack", new AlertAckRequest(null, null), JsonOpts)).EnsureSuccessStatusCode();
        var publicView = await guest.GetStringAsync($"/api/public/missions/{m.Mission.Id}");
        Assert.DoesNotContain("alex", publicView);
        var guestAlerts = (await guest.GetFromJsonAsync<MissionView>($"/api/public/missions/{m.Mission.Id}", JsonOpts))!.Mission.RecentAlerts;
        Assert.All(guestAlerts, a => Assert.NotNull(a.AcknowledgedAt));

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/missions/nope/alerts/ack", new AlertAckRequest(null, null), JsonOpts)).StatusCode);
    }

    [Fact]
    public async Task Finding_work_order_lifecycle_with_rbac()
    {
        var inspector = Client("alex");
        var supervisor = Client("dana", supervisor: true);
        var m = await PlanAsync(inspector, "DRN-API-3");
        var id = await ReportFindingAsync(inspector, m, 4);

        var queue = await inspector.GetFromJsonAsync<List<FindingView>>("/api/findings?status=open", JsonOpts);
        Assert.Contains(queue!, f => f.Finding.Id == id && f.Finding.Status == FindingStatus.New);

        // Inspectors can't dismiss…
        var denied = await inspector.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Dismissed, null, null), JsonOpts);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // …but can triage and assign, which leaves an audit trail.
        (await inspector.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Triaged, "Confirmed on imagery", null), JsonOpts)).EnsureSuccessStatusCode();
        var noAssignee = await inspector.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Assigned, null, null), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, noAssignee.StatusCode);
        var offRoster = await inspector.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Line Crew 4"), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, offRoster.StatusCode);
        var assigned = await inspector.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "lenny leonardo"), JsonOpts);
        var view = (await assigned.Content.ReadFromJsonAsync<FindingView>(JsonOpts))!;
        Assert.Equal("Lenny Leonardo (Sector 7G Line Crew)", view.Finding.AssignedTo);

        var crews = await inspector.GetFromJsonAsync<List<CrewView>>("/api/crews", JsonOpts);
        Assert.True(crews!.Single(c => c.Crew.Id == "sector-7g").OpenWorkOrders >= 1);
        Assert.Equal(["alex@gridsafety.dev", "alex@gridsafety.dev"], view.Finding.History.Select(h => h.By));

        // Supervisors can dismiss a different one.
        var other = await ReportFindingAsync(inspector, m, 2);
        var ok = await supervisor.PostAsJsonAsync($"/api/findings/{other}/status", new FindingStatusRequest(FindingStatus.Dismissed, "False positive: shadow", null), JsonOpts);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Illegal_finding_transition_is_rejected()
    {
        var c = Client();
        var m = await PlanAsync(c, "DRN-API-4");
        var id = await ReportFindingAsync(c, m, 3);
        var res = await c.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Resolved, null, null), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Ingest_rejects_unknown_mission()
    {
        var res = await Client().PostAsJsonAsync("/api/ingest", new DroneMessage { Kind = MessageKinds.Landed, DroneId = "X", MissionId = "nope" }, JsonOpts);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public void Parses_flattened_group_claims() =>
        Assert.Equal(["Supervisors", "Pilots"], UserResolver.ParseGroups("[Supervisors Pilots]"));

    [Fact]
    public async Task Demo_flight_is_supervisor_only_flies_to_completion_and_runs_one_at_a_time()
    {
        var inspector = await Client().PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest(null), JsonOpts);
        Assert.Equal(HttpStatusCode.Forbidden, inspector.StatusCode);

        var sup = Client("sam", supervisor: true);
        var unknown = await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest("NOPE"), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var res = await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest("CLAY-MEAD-115"), JsonOpts);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var launched = (await res.Content.ReadFromJsonAsync<MissionView>(JsonOpts))!;
        Assert.Equal(DemoFlight.DroneId, launched.Mission.DroneId);
        Assert.Matches(@"^[A-Z][a-z]+ [A-Z][a-z]+ \d{2}$", launched.Mission.Name);

        MissionView? m = null;
        for (var i = 0; i < 100 && m?.Mission.Status != MissionStatus.Completed; i++)
        {
            await Task.Delay(100);
            m = await sup.GetFromJsonAsync<MissionView>($"/api/missions/{launched.Mission.Id}", JsonOpts);
        }
        Assert.Equal(MissionStatus.Completed, m!.Mission.Status);
        Assert.True(m.Coverage > 0.8, $"coverage {m.Coverage}");
        Assert.True(m.Mission.FindingCount >= 1);

        // While the demo drone is busy, a second launch is refused.
        var manual = await sup.PostAsJsonAsync("/api/missions", new PlanMissionRequest("PITT-MART-115", DemoFlight.DroneId, "Pat"), JsonOpts);
        var busy = (await manual.Content.ReadFromJsonAsync<MissionView>(JsonOpts))!;
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest(null), JsonOpts)).StatusCode);
        (await sup.PostAsJsonAsync($"/api/missions/{busy.Mission.Id}/status", new MissionStatusRequest(MissionStatus.Aborted, null), JsonOpts)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Each_demo_flight_gets_a_new_name()
    {
        var sup = Client("dana", supervisor: true);
        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var res = await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest("CLAY-MEAD-115"), JsonOpts);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var launched = await res.Content.ReadFromJsonAsync<MissionView>(JsonOpts);
            names.Add(launched!.Mission.Name!);
            await WaitForLandingAsync(sup, launched.Mission.Id);
        }
        Assert.Equal(3, names.Distinct().Count());
    }

    private static async Task WaitForLandingAsync(HttpClient c, string missionId)
    {
        for (var i = 0; i < 100; i++)
        {
            var m = await c.GetFromJsonAsync<MissionView>($"/api/missions/{missionId}", JsonOpts);
            if (Mission.Workflow.IsTerminal(m!.Mission.Status)) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Demo flight {missionId} never landed");
    }
}
