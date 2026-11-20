using GridSafety.Core;
using GridSafety.Core.Simulation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

public static partial class Endpoints
{
    /// <summary>Simulated flights that may be airborne at once (demo flights included), to keep an open demo cheap.</summary>
    public const int MaxSimulatedFlights = 3;

    /// <summary>
    /// Supervisor-only, on demand: flies a planned mission with a simulated drone over its circuit, under its own drone ID,
    /// through the same pipeline as a real flight. The mission goes In flight on the first telemetry, as usual.
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

        var airborne = (await repo.ListMissionsAsync(200, ct))
            .Count(m => m.SimulatedAt is not null && m.Id != mission.Id && !Mission.Workflow.IsTerminal(m.Status) && !DemoFlight.IsStale(m, now));
        if (airborne >= MaxSimulatedFlights)
            return TypedResults.Conflict($"{airborne} simulated flights are already in the air. Try again when one lands.");

        // Claim the launch first; the versioned save stops a double click from launching two flights.
        mission.SimulatedAt = now;
        mission.UpdatedAt = now;
        try { await repo.SaveMissionAsync(mission, ct); }
        catch (ConcurrencyException) { return TypedResults.Conflict($"{label} was just changed. Reload and try again."); }

        try
        {
            var sim = await simulation.GetAsync(ct);
            await launcher.LaunchAsync(new DemoFlightLaunch(mission.Id, circuit.Id, mission.DroneId, sim.FindingCountForNextFlight(Random.Shared)), ct);
        }
        catch
        {
            mission.SimulatedAt = null;
            await repo.SaveMissionAsync(mission, CancellationToken.None);
            throw;
        }
        return TypedResults.Accepted($"/api/missions/{mission.Id}", ToView(mission, circuits));
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
