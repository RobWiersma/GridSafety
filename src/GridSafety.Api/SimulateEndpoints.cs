using GridSafety.Core;
using GridSafety.Core.Simulation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

public static partial class Endpoints
{
    /// <summary>Simulated flights (or outings of them) that may be airborne at once (demo flights included), to keep an open demo cheap.</summary>
    public const int MaxSimulatedFlights = 3;

    /// <summary>The most flights one outing can book: a crew's charged batteries for a morning on the line.</summary>
    public const int MaxFlightsPerOuting = 6;

    /// <summary>Simulated outings still going: an outing counts once, however many of its flights are still to fly.</summary>
    private static int AirborneOutings(IEnumerable<Mission> recent, DateTimeOffset now, string? except = null) =>
        recent.Where(m => m.SimulatedAt is not null && !Mission.Workflow.IsTerminal(m.Status) && m.OutingKey != except)
            .GroupBy(m => m.OutingKey).Count(g => g.Any(m => !DemoFlight.IsStale(m, now)));

    /// <summary>
    /// Supervisor-only, on demand: flies a planned mission with a simulated drone over its circuit, under its own drone ID,
    /// through the same pipeline as a real flight. The mission goes In flight on the first telemetry, as usual. A flight
    /// booked in an outing launches the outing: every flight of it still planned, in order, with a battery swap between.
    /// </summary>
    private static async Task<Results<Accepted<MissionView>, NotFound, Conflict<string>, ProblemHttpResult>> SimulateMission(
        string id, HttpContext ctx, IGridRepository repo, IResourceStore resources, ISimulationSettingsStore simulation, ICircuitCatalog circuits, IDemoFlightLauncher launcher,
        TimeProvider clock, CancellationToken ct)
    {
        if (!User(ctx).IsSupervisor)
            return TypedResults.Problem("Only supervisors can launch simulated flights.", statusCode: StatusCodes.Status403Forbidden);
        if (!launcher.Enabled)
            return TypedResults.Problem("Simulated flights are not configured for this deployment.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var mission = await repo.GetMissionAsync(id, ct);
        if (mission is null) return TypedResults.NotFound();
        var label = mission.Name ?? mission.Id;
        if (mission.Status != MissionStatus.Planned)
            return TypedResults.Conflict($"{label} is {mission.Status.ToString().ToLowerInvariant()}; only planned missions can be simulated.");
        var now = clock.GetUtcNow();
        if (mission.SimulatedAt is { } launched && now - launched < DemoFlight.StaleAfter)
            return TypedResults.Conflict($"{label} is already taking off.");
        if (circuits.Find(mission.CircuitId) is not { } circuit)
            return TypedResults.Conflict($"{label} is planned over a circuit that no longer exists.");
        if (await GroundedAsync(repo, await resources.GetAsync(ct), mission.DroneId, now, ct) is { } grounded) return TypedResults.Conflict(grounded);

        var recent = await repo.ListMissionsAsync(1000, ct);
        if (recent.FirstOrDefault(m => m.Status == MissionStatus.InFlight && m.DroneId.Equals(mission.DroneId, StringComparison.OrdinalIgnoreCase)) is { } up)
            return TypedResults.Conflict($"{mission.DroneId} is in the air on {up.Name ?? up.Id}. Try again when it lands.");
        var airborne = AirborneOutings(recent, now, except: mission.OutingKey);
        if (airborne >= MaxSimulatedFlights)
            return TypedResults.Conflict($"{airborne} simulated flights are already in the air. Try again when one lands.");

        // The whole outing goes, in order; a flight on its own is an outing of one.
        var flights = mission.OutingId is null ? [mission]
            : recent.Where(m => m.OutingId == mission.OutingId && m.Status == MissionStatus.Planned && m.Id != mission.Id).Append(mission)
                .OrderBy(m => m.OutingFlight).ToList();

        // Claim the launch first; the versioned save stops a double click from launching two flights.
        var claimed = new List<Mission>();
        foreach (var f in flights)
        {
            f.SimulatedAt = now;
            f.UpdatedAt = now;
            try
            {
                await repo.SaveMissionAsync(f, ct);
                claimed.Add(f);
            }
            catch (ConcurrencyException)
            {
                await ReleaseAsync(claimed);
                return TypedResults.Conflict($"{f.Name ?? f.Id} was just changed. Reload and try again.");
            }
        }

        try
        {
            var sim = await simulation.GetAsync(ct);
            var launches = flights.Select(f => new DemoFlightLaunch(f.Id, circuit.Id, f.DroneId, sim.FindingCountForNextFlight(Random.Shared), f.Leg)).ToList();
            await launcher.LaunchAsync(launches[0] with { Then = launches.Count > 1 ? [.. launches.Skip(1)] : null }, ct);
        }
        catch
        {
            await ReleaseAsync(claimed);
            throw;
        }
        return TypedResults.Accepted($"/api/missions/{mission.Id}", ToView(mission, circuits));

        async Task ReleaseAsync(IEnumerable<Mission> taken)
        {
            foreach (var f in taken)
            {
                f.SimulatedAt = null;
                await repo.SaveMissionAsync(f, CancellationToken.None);
            }
        }
    }

    private static void MapSimulationSettings(RouteGroupBuilder secured)
    {
        secured.MapGet("/simulation-settings", async (ISimulationSettingsStore store, CancellationToken ct) => TypedResults.Ok(await store.GetAsync(ct)));
        secured.MapPut("/simulation-settings", PutSimulationSettings);
    }

    private static async Task<IResult> PutSimulationSettings(
        SimulationSettings body, HttpContext ctx, ISimulationSettingsStore store, TimeProvider clock, CancellationToken ct)
    {
        var user = User(ctx);
        if (!user.IsSupervisor)
            return TypedResults.Problem("Only supervisors can change simulation settings.", statusCode: StatusCodes.Status403Forbidden);
        if (SimulationSettings.Validate(body) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
        body.UpdatedBy = user.DisplayName;
        body.UpdatedAt = clock.GetUtcNow();
        try
        {
            await store.SaveAsync(body, ct);
        }
        catch (ConcurrencyException)
        {
            return TypedResults.Conflict("Someone else changed the simulation settings. Reload to see their changes.");
        }
        return TypedResults.Ok(body);
    }
}
