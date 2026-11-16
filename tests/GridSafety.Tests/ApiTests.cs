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
        var assigned = await inspector.PostAsJsonAsync($"/api/findings/{id}/status", new FindingStatusRequest(FindingStatus.Assigned, null, "Line Crew 4"), JsonOpts);
        var view = (await assigned.Content.ReadFromJsonAsync<FindingView>(JsonOpts))!;
        Assert.Equal("Line Crew 4", view.Finding.AssignedTo);
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
}
