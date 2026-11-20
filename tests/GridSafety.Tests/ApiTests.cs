using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSafety.Api;
using GridSafety.Core;
using GridSafety.Core.Simulation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GridSafety.Tests;

/// <summary>Full HTTP round-trips through the real pipeline with the in-memory store and dev auth.</summary>
public class ApiTests(ApiTests.Factory factory) : IClassFixture<ApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public FakeAerialSource Aerial { get; } = new();
        public FakeRouteSource Routes { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development")
                .UseSetting("GridSafety:DemoTickMs", "0") // demo flights finish instantly
                .ConfigureTestServices(s =>
                {
                    s.AddSingleton<IAerialSource>(Aerial); // never call USGS from tests
                    s.AddSingleton<IRouteSource>(Routes);  // nor OSRM
                });
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Client(string user = "alex", bool supervisor = false)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Dev-User", user);
        if (supervisor) c.DefaultRequestHeaders.Add("X-Dev-Role", "Supervisor");
        return c;
    }

    /// <summary>
    /// Test flights over a long line land instantly but count their full time in the air, so the demo drone soon comes due
    /// for its routine inspection. Logging a service first means each test flight starts fresh from one.
    /// </summary>
    private static async Task FreshFromInspectionAsync(HttpClient supervisor) =>
        (await supervisor.PostAsJsonAsync($"/api/resources/drones/{DemoFlight.DroneId}/maintenance",
            new MaintenanceRequest(MaintenanceKind.Service, null, "Test: fresh from inspection"), JsonOpts)).EnsureSuccessStatusCode();

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
        await FreshFromInspectionAsync(sup);
        var unknown = await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest("NOPE"), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // No clean-flight roll here: this flight must find something.
        var sim = (await sup.GetFromJsonAsync<SimulationSettings>("/api/simulation-settings", JsonOpts))!;
        var neverClean = await sup.PutAsJsonAsync("/api/simulation-settings", new SimulationSettings { Version = sim.Version, CleanFlightPercent = 0 }, JsonOpts);
        neverClean.EnsureSuccessStatusCode();
        var res = await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest("CLAY-MEAD-115"), JsonOpts);
        var restored = (await neverClean.Content.ReadFromJsonAsync<SimulationSettings>(JsonOpts))!;
        (await sup.PutAsJsonAsync("/api/simulation-settings", new SimulationSettings { Version = restored.Version, CleanFlightPercent = sim.CleanFlightPercent }, JsonOpts))
            .EnsureSuccessStatusCode();
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
            await FreshFromInspectionAsync(sup);
            var res = await sup.PostAsJsonAsync("/api/demo-flights", new DemoFlightRequest("CLAY-MEAD-115"), JsonOpts);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var launched = await res.Content.ReadFromJsonAsync<MissionView>(JsonOpts);
            names.Add(launched!.Mission.Name!);
            await WaitForLandingAsync(sup, launched.Mission.Id);
        }
        Assert.Equal(3, names.Distinct().Count());
    }

    [Fact]
    public async Task Resources_list_the_fleet_and_crews_for_everyone()
    {
        var guest = await factory.CreateClient().GetFromJsonAsync<ResourcesView>("/api/public/resources", JsonOpts);
        Assert.Contains(guest!.Drones, d => d.Drone.Id == DemoFlight.DroneId);
        var res = await Client().GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts);
        Assert.Contains(res!.Drones, d => d.Drone.Id == DemoFlight.DroneId);
        Assert.Contains(res.FlightCrews, c => c.Crew.Id == "air-1" && c.Crew.Members.Any(m => m.Role == FlightCrew.PilotRole));
        Assert.Contains(res.GroundCrews, c => c.Crew.Id == "sector-7g");
    }

    [Fact]
    public async Task Guests_can_read_the_reports_but_not_log_maintenance()
    {
        var guest = factory.CreateClient();
        (await guest.GetAsync("/api/public/reports/activity")).EnsureSuccessStatusCode();
        (await guest.GetAsync("/api/public/reports/rates")).EnsureSuccessStatusCode();
        (await guest.GetAsync($"/api/public/resources/drones/{DemoFlight.DroneId}/report")).EnsureSuccessStatusCode();
        (await guest.GetAsync("/api/public/resources/vehicles/T-011/report")).EnsureSuccessStatusCode();
        var write = await guest.PostAsJsonAsync("/api/public/resources/vehicles/T-011/maintenance", new { kind = "Repair", hours = 1 }, JsonOpts);
        Assert.False(write.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_yard_report_lists_trucks_and_puts_jobs_from_before_trucks_on_one_line()
    {
        var yard = CrewRoster.Depots[^1];
        var at = DateTimeOffset.UtcNow.AddDays(-3);
        var f = new Finding
        {
            Id = Ids.New(), MissionId = "m-old", CircuitId = "c1", StructureId = "OLD-001", StructureLabel = "OLD-001",
            Type = FindingType.ConductorDamage, Severity = 3, Location = yard.Location, CreatedAt = at, UpdatedAt = at,
            Status = FindingStatus.Resolved,
            Dispatch = new FieldDispatch
            {
                Assignee = "Carl Carlsen (Test Crew)", CrewId = "test", Member = "Carl Carlsen", DepotId = yard.Id, DepotName = yard.Name,
                Depot = yard.Location, Site = yard.Location, SiteLabel = "OLD-001", From = yard.Location, FromLabel = yard.Name,
                Route = [yard.Location], ReturnRoute = [yard.Location],
                DepartedAt = at, ArrivesAt = at.AddMinutes(1), RepairedAt = at.AddMinutes(2), ReturnsAt = at.AddMinutes(3),
            },
        };
        await factory.Services.GetRequiredService<IGridRepository>().SaveFindingAsync(f);

        var report = await Client().GetFromJsonAsync<DepotReportView>($"/api/resources/depots/{yard.Id}/report", JsonOpts);
        Assert.DoesNotContain(report!.Work.Breakdown, b => b.Name == "Carl Carlsen");
        var none = Assert.Single(report.Work.Breakdown, b => b.Name == "No truck");
        Assert.Equal(1, none.Jobs);
    }

    [Fact]
    public async Task Only_supervisors_set_pay_and_crew_edits_keep_it()
    {
        const string path = "/api/resources/ground-crews/sector-7g/members/Lenny%20Leonardo/pay";
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("alex").PutAsJsonAsync(path, new PayRateRequest(150), JsonOpts)).StatusCode);

        var sup = Client("dana", supervisor: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync(path, new PayRateRequest(5), JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await sup.PutAsJsonAsync("/api/resources/ground-crews/sector-7g/members/Nobody/pay", new PayRateRequest(150), JsonOpts)).StatusCode);
        (await sup.PutAsJsonAsync(path, new PayRateRequest(150), JsonOpts)).EnsureSuccessStatusCode();

        static CrewMember? Lenny(ResourcesView? r) => r!.GroundCrews.Single(c => c.Crew.Id == "sector-7g").Crew.Members.FirstOrDefault(m => m.Name == "Lenny Leonardo");
        var res = await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts);
        Assert.Equal(150, Lenny(res)!.PayRate);
        Assert.Equal([150.0], Lenny(res)!.PayHistory!.Select(c => c.Rate));
        Assert.Equal(150, (await sup.GetFromJsonAsync<MemberShiftReports>("/api/resources/ground-crews/sector-7g/members/Lenny%20Leonardo/report", JsonOpts))!.PayRate);

        // Editing the crew (which sends no rates) keeps it.
        var crew = res!.GroundCrews.Single(c => c.Crew.Id == "sector-7g").Crew;
        var edit = crew with { Members = [.. crew.Members.Select(m => m with { PayRate = null })] };
        (await sup.PutAsJsonAsync("/api/resources/ground-crews/sector-7g", edit, JsonOpts)).EnsureSuccessStatusCode();
        Assert.Equal(150, Lenny(await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts))!.PayRate);
        Assert.Single(Lenny(await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts))!.PayHistory!);

        // Guests don't see pay.
        var guest = await factory.CreateClient().GetStringAsync("/api/public/resources");
        Assert.DoesNotContain("payRate", guest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payHistory", guest, StringComparison.OrdinalIgnoreCase);

        // Back to the standard rate.
        (await sup.PutAsJsonAsync(path, new PayRateRequest(null), JsonOpts)).EnsureSuccessStatusCode();
        var back = Lenny(await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts))!;
        Assert.Null(back.PayRate);
        Assert.Equal([150.0, null], back.PayHistory!.Select(c => c.Rate));
    }

    [Fact]
    public async Task Supervisors_edit_an_employee_record_and_pay_in_one_go()
    {
        const string path = "/api/resources/flight-crews/air-2/members/Troy%20McClaire";
        var profile = new EmployeeProfile("E-2047", new DateOnly(2019, 4, 1), "(925) 555-0142", "troy@example.com", "Antioch",
            "Pat McClaire (spouse)", "925-555-0199", "FAA Part 107", new DateOnly(2027, 3, 31), "Prefers early shifts");
        var edit = new EmployeeEdit("Troy McClaire", FlightCrew.ObserverRole, null, null, profile, 62.5);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("alex").PutAsJsonAsync(path, edit, JsonOpts)).StatusCode);

        var sup = Client("dana", supervisor: true);
        var bad = await sup.PutAsJsonAsync(path, edit with { Profile = profile with { Email = "not an email" } }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync(path, edit with { PayRate = 1000 }, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await sup.PutAsJsonAsync("/api/resources/flight-crews/air-2/members/Nobody", edit, JsonOpts)).StatusCode);

        (await sup.PutAsJsonAsync(path, edit, JsonOpts)).EnsureSuccessStatusCode();
        var troy = (await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts))!
            .FlightCrews.Single(c => c.Crew.Id == "air-2").Crew.Members.Single(m => m.Name == "Troy McClaire");
        Assert.Equal(profile, troy.Profile);
        Assert.Equal(62.5, troy.PayRate);

        var guest = await factory.CreateClient().GetStringAsync("/api/public/resources");
        Assert.DoesNotContain("E-2047", guest, StringComparison.Ordinal);
        Assert.DoesNotContain("profile", guest, StringComparison.OrdinalIgnoreCase);

        // Back to standard pay and an empty record.
        (await sup.PutAsJsonAsync(path, edit with { Profile = new EmployeeProfile(), PayRate = null }, JsonOpts)).EnsureSuccessStatusCode();
        troy = (await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts))!
            .FlightCrews.Single(c => c.Crew.Id == "air-2").Crew.Members.Single(m => m.Name == "Troy McClaire");
        Assert.Null(troy.Profile);
        Assert.Null(troy.PayRate);
    }

    [Fact]
    public async Task Only_supervisors_change_resources()
    {
        var drone = new DroneAsset("DRN-RBAC", "Test quad", "Test yard");
        var denied = await Client("alex").PutAsJsonAsync("/api/resources/drones/DRN-RBAC", drone, JsonOpts);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var sup = Client("dana", supervisor: true);
        var invalid = await sup.PutAsJsonAsync("/api/resources/drones/DRN-RBAC", drone with { Model = "" }, JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        (await sup.PutAsJsonAsync("/api/resources/drones/DRN-RBAC", drone, JsonOpts)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await sup.DeleteAsync("/api/resources/drones/DRN-RBAC")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sup.DeleteAsync("/api/resources/drones/DRN-RBAC")).StatusCode);
    }

    [Fact]
    public async Task Grounded_drones_cannot_be_planned_and_flying_drones_cannot_be_grounded()
    {
        var sup = Client("dana", supervisor: true);
        var drone = new DroneAsset("DRN-GND", "Test quad", "Test yard", DroneAssetStatus.Maintenance);
        (await sup.PutAsJsonAsync("/api/resources/drones/DRN-GND", drone, JsonOpts)).EnsureSuccessStatusCode();

        var plan = await sup.PostAsJsonAsync("/api/missions", new PlanMissionRequest("PITT-MART-115", "DRN-GND", "Edna Krabble"), JsonOpts);
        Assert.Equal(HttpStatusCode.Conflict, plan.StatusCode);

        (await sup.PutAsJsonAsync("/api/resources/drones/DRN-GND", drone with { Status = DroneAssetStatus.Active }, JsonOpts)).EnsureSuccessStatusCode();
        var m = await PlanAsync(sup, "DRN-GND");
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PutAsJsonAsync("/api/resources/drones/DRN-GND", drone, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sup.DeleteAsync("/api/resources/drones/DRN-GND")).StatusCode);

        var res = await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts);
        var view = res!.Drones.Single(d => d.Drone.Id == "DRN-GND");
        Assert.Equal(m.Mission.Id, view.ActiveMissionId);
        Assert.Equal(1, view.Missions);

        (await sup.PostAsJsonAsync($"/api/missions/{m.Mission.Id}/status", new MissionStatusRequest(MissionStatus.Aborted, null), JsonOpts)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await sup.DeleteAsync("/api/resources/drones/DRN-GND")).StatusCode);
    }

    [Fact]
    public async Task Flight_crews_need_a_pilot_and_count_their_missions()
    {
        var sup = Client("dana", supervisor: true);
        var noPilot = new FlightCrew("air-test", "Test Air Ops", "Test yard", [new("Ralph Wiggum", FlightCrew.ObserverRole)]);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync("/api/resources/flight-crews/air-test", noPilot, JsonOpts)).StatusCode);

        var crew = noPilot with { Members = [new("Chief Wigglesworth", FlightCrew.PilotRole), new("Ralph Wiggum", FlightCrew.ObserverRole)] };
        (await sup.PutAsJsonAsync("/api/resources/flight-crews/air-test", crew, JsonOpts)).EnsureSuccessStatusCode();
        var plan = await sup.PostAsJsonAsync("/api/missions", new PlanMissionRequest("PITT-MART-115", "DRN-AIRTEST", "Chief Wigglesworth"), JsonOpts);
        Assert.Equal(HttpStatusCode.Created, plan.StatusCode);

        var res = await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts);
        var view = res!.FlightCrews.Single(c => c.Crew.Id == "air-test");
        Assert.Equal(1, view.Missions);
        Assert.Equal(1, view.ActiveMissions);
        Assert.Equal(HttpStatusCode.NoContent, (await sup.DeleteAsync("/api/resources/flight-crews/air-test")).StatusCode);
    }

    [Fact]
    public async Task Ground_crew_edits_cannot_orphan_open_work_orders()
    {
        var sup = Client("dana", supervisor: true);
        var crew = new Crew("test-gc", "Test Ground Crew", "Testing", [FindingType.CrossarmDamage], [new("Apu Nahasa", "Foreman"), new("Sanjay Nahasa", "Lineman")],
            DepotId: "concord");
        (await sup.PutAsJsonAsync("/api/resources/ground-crews/test-gc", crew, JsonOpts)).EnsureSuccessStatusCode();

        var m = await PlanAsync(sup, "DRN-GC");
        var id = await ReportFindingAsync(sup, m, 3);
        (await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Triaged, null, null), JsonOpts)).EnsureSuccessStatusCode();
        var assigned = await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Apu Nahasa"), JsonOpts);
        Assert.Equal("Apu Nahasa (Test Ground Crew)", (await assigned.Content.ReadFromJsonAsync<FindingView>(JsonOpts))!.Finding.AssignedTo);

        // Renaming the crew or dropping Apu would leave the work order pointing at nobody.
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PutAsJsonAsync("/api/resources/ground-crews/test-gc", crew with { Name = "Renamed Crew" }, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PutAsJsonAsync("/api/resources/ground-crews/test-gc", crew with { Members = [new("Sanjay Nahasa", "Lineman")] }, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sup.DeleteAsync("/api/resources/ground-crews/test-gc")).StatusCode);

        // Adding someone is fine; an off-duty crew takes no new work.
        (await sup.PutAsJsonAsync("/api/resources/ground-crews/test-gc", crew with { Members = [.. crew.Members, new("Manjula Nahasa", "Apprentice")], Status = CrewStatus.OffDuty }, JsonOpts)).EnsureSuccessStatusCode();
        var other = await ReportFindingAsync(sup, m, 3);
        (await sup.PostAsJsonAsync($"/api/findings/{other}/status", new FindingStatusRequest(FindingStatus.Triaged, null, null), JsonOpts)).EnsureSuccessStatusCode();
        var offDuty = await sup.PostAsJsonAsync($"/api/findings/{other}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Sanjay Nahasa"), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, offDuty.StatusCode);

        // Once the work order is resolved the crew can go.
        (await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Resolved, "Fixed", null), JsonOpts)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await sup.DeleteAsync("/api/resources/ground-crews/test-gc")).StatusCode);
    }

    [Fact]
    public async Task The_schedule_ranks_waiting_work_and_sends_what_can_start_now()
    {
        var sup = Client("dana", supervisor: true);
        var m = await PlanAsync(sup, "DRN-SCHED");
        var id = await ReportFindingAsync(sup, m, 5);
        Assert.DoesNotContain((await sup.GetFromJsonAsync<ScheduleView>("/api/schedule", JsonOpts))!.Jobs, j => j.FindingId == id); // not triaged yet
        (await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Triaged, null, null), JsonOpts)).EnsureSuccessStatusCode();

        var schedule = (await sup.GetFromJsonAsync<ScheduleView>("/api/schedule", JsonOpts))!;
        var job = schedule.Jobs.Single(j => j.FindingId == id);
        Assert.Equal(["Critical", "115 kV line"], job.Reasons);
        Assert.Equal(job.FoundAt.AddDays(1), job.RepairBy);
        Assert.All(schedule.Jobs.Zip(schedule.Jobs.Skip(1)), p => Assert.True(p.First.Severity >= p.Second.Severity));
        Assert.Equal(Enumerable.Range(1, schedule.Jobs.Count), schedule.Jobs.Select(j => j.Rank));

        // Guests can look but not send anyone.
        var guest = factory.CreateClient();
        Assert.Contains(id, await guest.GetStringAsync("/api/public/schedule"), StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, (await guest.PostAsync("/api/public/schedule/dispatch", null)).StatusCode);

        // Sending just this one goes through the same checks as assigning it by hand.
        Assert.True(job.StartsNow, "someone qualified should be free for a critical crossarm job");
        var result = await (await Client("alex").PostAsync($"/api/schedule/dispatch?findingId={id}", null)).Content.ReadFromJsonAsync<ScheduleDispatchResult>(JsonOpts);
        Assert.Equal(job.Assignee, result!.Dispatched.Single().Assignee);
        var f = (await sup.GetFromJsonAsync<FindingView>($"/api/findings/{id}", JsonOpts))!.Finding;
        Assert.Equal((FindingStatus.Assigned, job.Assignee), (f.Status, f.AssignedTo));
        Assert.NotNull(f.Dispatch);
        Assert.Equal("Dispatched from the schedule.", f.History[^1].Note);
        Assert.DoesNotContain((await sup.GetFromJsonAsync<ScheduleView>("/api/schedule", JsonOpts))!.Jobs, j => j.FindingId == id);
    }

    [Fact]
    public async Task Work_orders_go_only_to_qualified_people_nearest_first()
    {
        var sup = Client("dana", supervisor: true);
        var m = await PlanAsync(sup, "DRN-NEAR");
        var id = await ReportFindingAsync(sup, m, 3); // crossarm damage
        (await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Triaged, null, null), JsonOpts)).EnsureSuccessStatusCode();

        var options = (await sup.GetFromJsonAsync<List<AssigneeOption>>($"/api/findings/{id}/assignees", JsonOpts))!;
        var technicians = options.Where(o => o.Member is not null && o.BusyUntil is null).ToList(); // other tests may have people out
        Assert.NotEmpty(technicians);
        Assert.Equal(technicians.Select(o => o.DistanceKm).Order(), technicians.Select(o => o.DistanceKm));
        Assert.Contains(options, o => o.Member == "Barney Grumble"); // cross-trained on crossarms
        Assert.DoesNotContain(options, o => o.Member is "Otto Mannheim" or "Lisa Simsen" or "Willie MacTavish");
        Assert.Contains(options, o => o.Member is null && o.CrewId == "sector-7g");

        var view = (await sup.GetFromJsonAsync<FindingView>($"/api/findings/{id}", JsonOpts))!;
        Assert.Equal(technicians[0].Label, view.SuggestedCrew);

        foreach (var who in new[] { "Otto Mannheim", "Lisa Simsen", "Evergreen Terrace Vegetation Crew" })
        {
            var res = await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Assigned, null, who), JsonOpts);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("qualified", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Assigning_sends_a_truck_along_the_roads_and_nearby_jobs_line_up_behind_it()
    {
        var sup = Client("dana", supervisor: true);
        var crew = new Crew("busy-gc", "Busy Test Crew", "Testing", [FindingType.CrossarmDamage],
            [new("Jimbo Jones", "Lineman"), new("Kearney Zzyzwicz", "Lineman")], DepotId: "pittsburg");
        (await sup.PutAsJsonAsync("/api/resources/ground-crews/busy-gc", crew, JsonOpts)).EnsureSuccessStatusCode();
        var m = await PlanAsync(sup, "DRN-BUSY");
        var first = await ReportFindingAsync(sup, m, 3);
        var second = await ReportFindingAsync(sup, m, 3);
        foreach (var id in new[] { first, second })
            (await sup.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Triaged, null, null), JsonOpts)).EnsureSuccessStatusCode();

        var sent = await sup.PostAsJsonAsync($"/api/findings/{first}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Jimbo Jones"), JsonOpts);
        var view = (await sent.Content.ReadFromJsonAsync<FindingView>(JsonOpts))!;
        var d = view.Finding.Dispatch!;
        Assert.Equal("Jimbo Jones", d.Member);
        Assert.Equal("pittsburg", d.DepotId);
        Assert.True(d.OnRoads);
        Assert.Equal(3, d.Route.Count); // the fake router's dog-leg
        Assert.True(d.DepartedAt < d.ArrivesAt && d.ArrivesAt < d.RepairedAt && d.RepairedAt < d.ReturnsAt);

        var jobs = (await sup.GetFromJsonAsync<FieldJobsView>("/api/field-jobs", JsonOpts))!;
        Assert.Contains(jobs.Jobs, j => j.FindingId == first && j.Phase == FieldPhase.EnRoute);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/public/field-jobs")).StatusCode);

        // The second defect is on the same line, so Jimbo is offered it after his first job; his crew as a whole is busy.
        var options = (await sup.GetFromJsonAsync<List<AssigneeOption>>($"/api/findings/{second}/assignees", JsonOpts))!;
        var jimbo = options.Single(o => o.Member == "Jimbo Jones");
        Assert.Null(jimbo.BusyUntil);
        Assert.Equal(view.Finding.StructureLabel, jimbo.AfterJob);
        Assert.NotNull(options.Single(o => o.Member is null && o.CrewId == "busy-gc").BusyUntil);
        var crewRes = await sup.PostAsJsonAsync($"/api/findings/{second}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Busy Test Crew"), JsonOpts);
        Assert.Equal(HttpStatusCode.BadRequest, crewRes.StatusCode);
        Assert.Contains("out on a job elsewhere", await crewRes.Content.ReadAsStringAsync());

        var chained = await sup.PostAsJsonAsync($"/api/findings/{second}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Jimbo Jones"), JsonOpts);
        var next = (await chained.Content.ReadFromJsonAsync<FindingView>(JsonOpts))!.Finding.Dispatch!;
        var handedOver = (await sup.GetFromJsonAsync<FindingView>($"/api/findings/{first}", JsonOpts))!.Finding.Dispatch!;
        Assert.Equal(second, handedOver.NextFindingId);
        Assert.Equal(handedOver.RepairedAt, next.DepartedAt);
        Assert.Equal(d.Site, next.From);

        jobs = (await sup.GetFromJsonAsync<FieldJobsView>("/api/field-jobs", JsonOpts))!;
        Assert.Contains(jobs.Jobs, j => j.FindingId == second && j.Phase == FieldPhase.Queued);

        // Closing the queued job by hand sends him home after the first instead; closing the first then turns him round.
        (await sup.PostAsJsonAsync($"/api/findings/{second}/status", new FindingStatusRequest(FindingStatus.Resolved, "Done by another crew", null), JsonOpts)).EnsureSuccessStatusCode();
        var cancelled = (await sup.GetFromJsonAsync<FindingView>($"/api/findings/{second}", JsonOpts))!.Finding.Dispatch!;
        Assert.Equal(cancelled.DepartedAt, cancelled.RecalledAt);
        (await sup.PostAsJsonAsync($"/api/findings/{first}/status", new FindingStatusRequest(FindingStatus.Resolved, null, null), JsonOpts)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Ground_crews_need_a_real_depot()
    {
        var sup = Client("dana", supervisor: true);
        var crew = new Crew("depot-gc", "Depot Test Crew", "Testing", [FindingType.MissingHardware], [new("Kirk Van Houten", "Lineman")]);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync("/api/resources/ground-crews/depot-gc", crew, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync("/api/resources/ground-crews/depot-gc", crew with { DepotId = "atlantis" }, JsonOpts)).StatusCode);
        var badMember = crew with { DepotId = "concord", Members = [new("Kirk Van Houten", "Lineman", DepotId: "atlantis")] };
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync("/api/resources/ground-crews/depot-gc", badMember, JsonOpts)).StatusCode);

        var good = crew with { DepotId = "concord", Members = [new("Kirk Van Houten", "Lineman", [FindingType.MissingHardware, FindingType.DamagedInsulator], "antioch")] };
        (await sup.PutAsJsonAsync("/api/resources/ground-crews/depot-gc", good, JsonOpts)).EnsureSuccessStatusCode();
        var res = (await sup.GetFromJsonAsync<ResourcesView>("/api/resources", JsonOpts))!;
        var saved = res.GroundCrews.Single(c => c.Crew.Id == "depot-gc").Crew;
        Assert.Equal("antioch", saved.Members[0].DepotId);
        Assert.Equal([FindingType.MissingHardware, FindingType.DamagedInsulator], saved.Members[0].Skills);
        Assert.Contains(res.Depots, d => d.Id == "concord");
        Assert.Equal(HttpStatusCode.NoContent, (await sup.DeleteAsync("/api/resources/ground-crews/depot-gc")).StatusCode);
    }

    [Fact]
    public async Task Supervisors_can_fly_a_planned_mission_with_a_simulated_drone()
    {
        var inspector = Client("alex");
        var sup = Client("dana", supervisor: true);
        var m = await PlanAsync(inspector, "DRN-SIM");

        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsync($"/api/missions/{m.Mission.Id}/simulate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sup.PostAsync("/api/missions/nope/simulate", null)).StatusCode);

        var res = await sup.PostAsync($"/api/missions/{m.Mission.Id}/simulate", null);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await WaitForLandingAsync(sup, m.Mission.Id);

        var flown = (await sup.GetFromJsonAsync<MissionView>($"/api/missions/{m.Mission.Id}", JsonOpts))!.Mission;
        Assert.Equal(MissionStatus.Completed, flown.Status);
        Assert.Equal("DRN-SIM", flown.DroneId);
        Assert.NotNull(flown.SimulatedAt);
        Assert.NotEmpty((await sup.GetFromJsonAsync<List<TelemetryPoint>>($"/api/missions/{m.Mission.Id}/track", JsonOpts))!);

        // Only planned missions can be simulated.
        Assert.Equal(HttpStatusCode.Conflict, (await sup.PostAsync($"/api/missions/{m.Mission.Id}/simulate", null)).StatusCode);
    }

    [Fact]
    public async Task Opening_a_finding_fetches_its_aerial_photo_once_in_the_background()
    {
        var c = Client();
        var m = await PlanAsync(c, "DRN-AERIAL");
        var id = await ReportFindingAsync(c, m, 4);

        // Thumbnails only read the cache.
        Assert.Equal("unavailable", (await c.GetFromJsonAsync<AerialView>($"/api/findings/{id}/aerial?fetch=false", JsonOpts))!.Status);

        var first = await c.GetFromJsonAsync<AerialView>($"/api/findings/{id}/aerial", JsonOpts);
        Assert.Contains(first!.Status, new[] { "pending", "ready" });
        AerialView? view = first;
        for (var i = 0; i < 50 && view!.Status != "ready"; i++)
        {
            await Task.Delay(50);
            view = await c.GetFromJsonAsync<AerialView>($"/api/findings/{id}/aerial", JsonOpts);
        }
        Assert.Equal("ready", view!.Status);
        Assert.Contains("data:image/jpeg;base64,", view.Svg);

        // Guests see it too; a second finding on the same structure reuses the cached photo.
        var guest = await factory.CreateClient().GetFromJsonAsync<AerialView>($"/api/public/findings/{id}/aerial", JsonOpts);
        Assert.Equal("ready", guest!.Status);
        var calls = factory.Aerial.Calls;
        var again = await ReportFindingAsync(c, m, 3);
        Assert.Equal("ready", (await c.GetFromJsonAsync<AerialView>($"/api/findings/{again}/aerial", JsonOpts))!.Status);
        Assert.Equal(calls, factory.Aerial.Calls);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await c.GetAsync("/api/findings/nope/aerial")).StatusCode);
    }

    [Fact]
    public async Task Supervisors_set_how_many_findings_simulated_flights_report()
    {
        var inspector = Client("alex");
        var sup = Client("dana", supervisor: true);
        var defaults = await inspector.GetFromJsonAsync<SimulationSettings>("/api/simulation-settings", JsonOpts);
        Assert.Null(defaults!.FindingsPerFlight);

        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PutAsJsonAsync("/api/simulation-settings", new SimulationSettings { FindingsPerFlight = 2 }, JsonOpts)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sup.PutAsJsonAsync("/api/simulation-settings", new SimulationSettings { FindingsPerFlight = 99 }, JsonOpts)).StatusCode);
        var saved = await sup.PutAsJsonAsync("/api/simulation-settings", new SimulationSettings { Version = defaults.Version, FindingsPerFlight = 2 }, JsonOpts);
        saved.EnsureSuccessStatusCode();
        var version = (await saved.Content.ReadFromJsonAsync<SimulationSettings>(JsonOpts))!.Version;
        try
        {
            var m = await PlanAsync(sup, "DRN-COUNT");
            Assert.Equal(HttpStatusCode.Accepted, (await sup.PostAsync($"/api/missions/{m.Mission.Id}/simulate", null)).StatusCode);
            await WaitForLandingAsync(sup, m.Mission.Id);
            var findings = await sup.GetFromJsonAsync<List<FindingView>>($"/api/findings?status=all&missionId={m.Mission.Id}", JsonOpts);
            Assert.Equal(2, findings!.Count);
        }
        finally
        {
            // Back to random so other tests' flights aren't affected.
            (await sup.PutAsJsonAsync("/api/simulation-settings", new SimulationSettings { Version = version, FindingsPerFlight = null, CleanFlightPercent = defaults.CleanFlightPercent }, JsonOpts)).EnsureSuccessStatusCode();
        }
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
