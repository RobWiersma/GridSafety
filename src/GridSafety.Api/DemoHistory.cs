using System.Diagnostics.CodeAnalysis;
using GridSafety.Core;
using GridSafety.Core.Simulation;

namespace GridSafety.Api;

/// <summary>
/// Local runs only (in-memory storage, <c>GridSafety:SeedHistoryDays</c> &gt; 0): fills the store with that many past days
/// of simulated operations, so the reports have history to chart. Each weekday the drone crews fly a few circuits, the
/// flights turn up findings, a supervisor triages them, and repairs go to the nearest qualified technician with a free
/// truck, through the same dispatch rules as the console. Seeded from a fixed number, so every run looks the same. Routes
/// are straight lines (no calls to the road router). Never registered against DynamoDB.
/// </summary>
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Demo data, not security.")]
public sealed class DemoHistorySeeder(IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock,
    IConfiguration config, ILogger<DemoHistorySeeder> log) : IHostedService
{
    private const string Supervisor = "dana@gridsafety.dev";

    public async Task StartAsync(CancellationToken ct)
    {
        var days = Math.Clamp(config.GetValue<int>("GridSafety:SeedHistoryDays"), 0, 120);
        if (days == 0) return;
        var (missions, findings) = await SeedAsync(days, ct);
        log.LogInformation("Seeded {Days} days of demo history: {Missions} flights, {Findings} findings", days, missions, findings);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<(int Missions, int Findings)> SeedAsync(int days, CancellationToken ct)
    {
        var rng = new Random(20261120);
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        var drones = catalog.Drones.Where(d => d.Status == DroneAssetStatus.Active && d.Id != DemoFlight.DroneId).ToList();
        var pilots = catalog.FlightCrews.Where(c => c.Status == CrewStatus.Available).SelectMany(c => c.Pilots).Select(m => m.Name).ToList();
        var lines = circuits.All.Where(c => c.Structures.Count >= 12).ToList();
        if (drones.Count == 0 || pilots.Count == 0 || lines.Count == 0) return (0, 0);

        var missions = new List<Mission>();
        var findings = new List<Finding>();
        var triage = new List<(DateTimeOffset At, Finding Finding)>();
        var types = Enum.GetValues<FindingType>();

        for (var back = days; back >= 1; back--)
        {
            var day = Periods.DayOf(now).AddDays(-back);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var flights = weekend ? rng.Next(0, 2) : rng.Next(2, 6);
            // 7 am Pacific on that day.
            var morning = new DateTimeOffset(day.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero);
            morning -= Periods.OffsetAt(morning);

            for (var i = 0; i < flights; i++)
            {
                var circuit = lines[rng.Next(lines.Count)];
                var start = morning + TimeSpan.FromMinutes(rng.Next(0, 9 * 60));
                // One battery's stretch of the line: the flight due next, along it from end to end.
                var leg = circuit.Legs()[circuit.NextLeg(missions) - 1];
                var m = Mission.Plan(circuit, drones[rng.Next(drones.Count)].Id, pilots[rng.Next(pilots.Count)], Supervisor, start - TimeSpan.FromMinutes(20),
                    FlightNames.Generate(rng, missions.Select(x => x.Name)), leg.Number);
                m.MoveTo(MissionStatus.InFlight, start, m.Pilot, "First telemetry received");

                var run = circuit.Stretch(leg.Number).Structures.ToList();
                var aborted = rng.NextDouble() < 0.06;
                if (aborted) run = run.Take(Math.Max(2, run.Count / 3)).ToList();
                m.StructuresInspected.AddRange(run.Select(s => s.Id));
                m.DistanceFlownMeters = run.Zip(run.Skip(1), (a, b) => Geo.DistanceMeters(a.Location, b.Location)).Sum() * (1.02 + rng.NextDouble() * 0.04);
                m.LastPosition = run[^1].Location;
                var minutes = Math.Max(3, (int)Math.Round(FlightLegs.MinutesFor(m.DistanceFlownMeters)));
                m.BatteryPct = Math.Round(FlightPlan.BatteryAfter(TimeSpan.FromMinutes(minutes - 1)));

                var found = aborted ? 0 : rng.Next(0, 5);
                foreach (var s in run.OrderBy(_ => rng.Next()).Take(found))
                {
                    var at = start + TimeSpan.FromMinutes(rng.Next(1, minutes));
                    var severity = rng.NextDouble() switch { < 0.3 => 1, < 0.6 => 2, < 0.8 => 3, < 0.93 => 4, _ => 5 };
                    var f = new Finding
                    {
                        Id = Ids.New(), MissionId = m.Id, CircuitId = circuit.Id, StructureId = s.Id, StructureLabel = s.Label,
                        Type = types[rng.Next(types.Length)], Severity = severity, Confidence = Math.Round(0.7 + rng.NextDouble() * 0.27, 2),
                        Location = s.Location, CreatedAt = at, UpdatedAt = at,
                        TriageDueAt = TriageSla.WindowFor(severity) is { } window ? at + window : null,
                    };
                    findings.Add(f);
                    // Most get triaged within the shift; a few wait.
                    if (rng.NextDouble() < 0.92) triage.Add((at + TimeSpan.FromMinutes(rng.Next(10, severity >= 4 ? 90 : 360)), f));
                }
                m.FindingCount = found;
                m.MoveTo(aborted ? MissionStatus.Aborted : MissionStatus.Completed, start + TimeSpan.FromMinutes(minutes), m.Pilot,
                    aborted ? "Returned early: weak signal" : "Drone landed");
                missions.Add(m);
            }
        }

        // Triage in time order, so each dispatch sees who was already out.
        foreach (var (at, f) in triage.OrderBy(t => t.At))
        {
            if (at >= now) continue;
            if (f.Severity <= 1 && rng.NextDouble() < 0.4)
            {
                f.MoveTo(FindingStatus.Dismissed, at, Supervisor, "Within tolerance");
                continue;
            }
            f.MoveTo(FindingStatus.Triaged, at, Supervisor);
            if (f.Severity < 2) continue;
            await AssignAsync(f, findings, catalog, at, ct);
        }

        foreach (var f in findings) FieldWork.Settle(f, now);
        foreach (var m in missions) await repo.SaveMissionAsync(m, ct);
        foreach (var f in findings) await repo.SaveFindingAsync(f, ct);
        return (missions.Count, findings.Count);
    }

    /// <summary>The nearest technician who can take it now, as the console would offer first.</summary>
    private static async Task AssignAsync(Finding f, List<Finding> all, ResourceCatalog catalog, DateTimeOffset at, CancellationToken ct)
    {
        var hours = new FieldHours(FieldWork.History(all), at, f.Severity);
        var active = hours.History.Where(d => d.IsActive(at)).ToList();
        var trucks = catalog.Vehicles.Where(v => Upkeep.Truck(v, hours.History, catalog.Maintenance, at).Available).ToList();
        var who = CrewRoster.Candidates(catalog.GroundCrews, catalog.Depots, f.Type, f.Location, active, hours, trucks).FirstOrDefault(c => c.Available);
        if (who is null) return;
        var previous = who.After is { } after ? all.FirstOrDefault(x => ReferenceEquals(x.Dispatch, after)) : null;
        var dispatch = await FieldWork.PlanAsync(who, f, previous, StraightLineRouteSource.Instance, at, ct);
        if (dispatch is null) return;
        f.MoveTo(FindingStatus.Assigned, at, Supervisor, assignee: who.Label);
        f.Dispatch = dispatch;
    }
}
