using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>A fleet drone with its live state and how much it has flown (from the last 1,000 missions).</summary>
public sealed record DroneResourceView(
    DroneAsset Drone, DroneState? Live, string? ActiveMissionId, string? ActiveMissionName,
    int Missions, double FlightHours, double DistanceMeters, DateTimeOffset? LastFlight);

/// <summary>A flight crew with the missions flown by its pilots.</summary>
public sealed record FlightCrewView(FlightCrew Crew, int Missions, int ActiveMissions, DateTimeOffset? LastFlight);

public sealed record ResourcesView(IReadOnlyList<DroneResourceView> Drones, IReadOnlyList<FlightCrewView> FlightCrews, IReadOnlyList<CrewView> GroundCrews);

/// <summary>Drones, flight crews and ground crews. Everyone can see them; supervisors add, edit and remove them.</summary>
public static partial class Endpoints
{
    private static void MapResources(RouteGroupBuilder secured)
    {
        secured.MapGet("/resources", ListResources);
        secured.MapPut("/resources/drones/{id}", PutDrone);
        secured.MapDelete("/resources/drones/{id}", DeleteDrone);
        secured.MapPut("/resources/flight-crews/{id}", PutFlightCrew);
        secured.MapDelete("/resources/flight-crews/{id}", DeleteFlightCrew);
        secured.MapPut("/resources/ground-crews/{id}", PutGroundCrew);
        secured.MapDelete("/resources/ground-crews/{id}", DeleteGroundCrew);
    }

    private static async Task<Ok<ResourcesView>> ListResources(IGridRepository repo, IResourceStore resources, CancellationToken ct)
    {
        var catalog = await resources.GetAsync(ct);
        var missions = await repo.ListMissionsAsync(1000, ct);
        var live = (await repo.ListDronesAsync(ct)).ToDictionary(d => d.DroneId, StringComparer.OrdinalIgnoreCase);

        var drones = catalog.Drones.Select(d =>
        {
            var flown = missions.Where(m => m.DroneId.Equals(d.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var active = flown.FirstOrDefault(m => !Mission.Workflow.IsTerminal(m.Status));
            var hours = flown.Where(m => m.StartedAt is not null).Sum(m => ((m.EndedAt ?? m.UpdatedAt) - m.StartedAt!.Value).TotalHours);
            return new DroneResourceView(d, live.GetValueOrDefault(d.Id), active?.Id, active?.Name,
                flown.Count, Math.Round(Math.Max(0, hours), 1), flown.Sum(m => m.DistanceFlownMeters), flown.Max(m => (DateTimeOffset?)(m.StartedAt ?? m.CreatedAt)));
        }).ToList();

        var flightCrews = catalog.FlightCrews.Select(c =>
        {
            var flown = missions.Where(m => c.Pilots.Any(p => FlownBy(m, p))).ToList();
            return new FlightCrewView(c, flown.Count, flown.Count(m => !Mission.Workflow.IsTerminal(m.Status)),
                flown.Max(m => (DateTimeOffset?)(m.StartedAt ?? m.CreatedAt)));
        }).ToList();

        var open = OpenWorkOrders(await repo.ListFindingsAsync(1000, ct), catalog.GroundCrews);
        var groundCrews = catalog.GroundCrews.Select(c => new CrewView(c, open.GetValueOrDefault(c.Id))).ToList();
        return TypedResults.Ok(new ResourcesView(drones, flightCrews, groundCrews));
    }

    /// <summary>Missions store the pilot as text, e.g. "Edna Krabble" or "Edna Krabble (Part 107)".</summary>
    private static bool FlownBy(Mission m, CrewMember pilot) => m.Pilot.StartsWith(pilot.Name, StringComparison.OrdinalIgnoreCase);

    private static Task<IResult> PutDrone(string id, DroneAsset body, HttpContext ctx, IGridRepository repo, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            var drone = body with { Id = id.Trim().ToUpperInvariant(), Model = body.Model?.Trim()!, HomeBase = body.HomeBase?.Trim()!, Notes = Clean(body.Notes) };
            if (ResourceCatalog.Validate(drone) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
            if (drone.Status != DroneAssetStatus.Active && await ActiveMissionAsync(repo, drone.Id, ct) is { } m)
                return TypedResults.Conflict($"{drone.Id} is on mission {m.Name ?? m.Id}. Land it before taking it out of service.");
            Upsert(catalog.Drones, drone, d => d.Id.Equals(drone.Id, StringComparison.OrdinalIgnoreCase));
            return TypedResults.Ok(drone);
        }, ct);

    private static Task<IResult> DeleteDrone(string id, HttpContext ctx, IGridRepository repo, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            if (catalog.FindDrone(id) is not { } drone) return TypedResults.NotFound();
            if (await ActiveMissionAsync(repo, drone.Id, ct) is { } m)
                return TypedResults.Conflict($"{drone.Id} is on mission {m.Name ?? m.Id}. Land it before removing it.");
            catalog.Drones.Remove(drone);
            return TypedResults.NoContent();
        }, ct);

    private static Task<IResult> PutFlightCrew(string id, FlightCrew body, HttpContext ctx, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, catalog =>
        {
            var crew = body with { Id = id.Trim(), Name = body.Name?.Trim()!, HomeBase = body.HomeBase?.Trim()!, Members = CleanMembers(body.Members) };
            if (ResourceCatalog.Validate(crew) is { Count: > 0 } errors) return Task.FromResult<IResult>(TypedResults.ValidationProblem(errors));
            Upsert(catalog.FlightCrews, crew, c => c.Id == crew.Id);
            return Task.FromResult<IResult>(TypedResults.Ok(crew));
        }, ct);

    private static Task<IResult> DeleteFlightCrew(string id, HttpContext ctx, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, catalog =>
            Task.FromResult<IResult>(catalog.FlightCrews.RemoveAll(c => c.Id == id) > 0 ? TypedResults.NoContent() : TypedResults.NotFound()), ct);

    private static Task<IResult> PutGroundCrew(string id, Crew body, HttpContext ctx, IGridRepository repo, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            var crew = body with { Id = id.Trim(), Name = body.Name?.Trim()!, Specialty = body.Specialty?.Trim()!, Members = CleanMembers(body.Members), Handles = body.Handles?.Distinct().ToList()! };
            if (ResourceCatalog.Validate(crew) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
            if (catalog.GroundCrews.Any(c => c.Id != crew.Id && c.Name.Equals(crew.Name, StringComparison.OrdinalIgnoreCase)))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Another ground crew already has that name."] });

            // Work orders store "Member (Crew)"; an edit must not orphan open ones.
            var after = catalog.GroundCrews.Where(c => c.Id != crew.Id).Append(crew).ToList();
            var orphaned = await OrphanedWorkOrdersAsync(repo, catalog.GroundCrews, after, crew.Id, ct);
            if (orphaned > 0)
                return TypedResults.Conflict($"{orphaned} open work order(s) point at a name this edit changes or removes. Reassign them first.");
            Upsert(catalog.GroundCrews, crew, c => c.Id == crew.Id);
            return TypedResults.Ok(crew);
        }, ct);

    private static Task<IResult> DeleteGroundCrew(string id, HttpContext ctx, IGridRepository repo, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            if (catalog.GroundCrews.FirstOrDefault(c => c.Id == id) is not { } crew) return TypedResults.NotFound();
            var orphaned = await OrphanedWorkOrdersAsync(repo, catalog.GroundCrews, catalog.GroundCrews.Where(c => c.Id != id).ToList(), id, ct);
            if (orphaned > 0) return TypedResults.Conflict($"{crew.Name} has {orphaned} open work order(s). Reassign them first.");
            catalog.GroundCrews.Remove(crew);
            return TypedResults.NoContent();
        }, ct);

    /// <summary>Supervisor-only read-modify-write of the catalog, retried when another edit lands first.</summary>
    private static async Task<IResult> EditAsync(HttpContext ctx, IResourceStore resources, Func<ResourceCatalog, Task<IResult>> edit, CancellationToken ct)
    {
        if (!User(ctx).IsSupervisor)
            return TypedResults.Problem("Only supervisors can change resources.", statusCode: StatusCodes.Status403Forbidden);
        for (var attempt = 0; ; attempt++)
        {
            var catalog = await resources.GetAsync(ct);
            var result = await edit(catalog);
            if (result is not (Ok<DroneAsset> or Ok<FlightCrew> or Ok<Crew> or NoContent)) return result;
            try
            {
                await resources.SaveAsync(catalog, ct);
                return result;
            }
            catch (ConcurrencyException) when (attempt < 3)
            {
                await Task.Delay(50 * (attempt + 1), ct);
            }
            catch (ConcurrencyException ex)
            {
                return TypedResults.Conflict(ex.Message);
            }
        }
    }

    private static async Task<Mission?> ActiveMissionAsync(IGridRepository repo, string droneId, CancellationToken ct) =>
        (await repo.ListMissionsAsync(200, ct)).FirstOrDefault(m => m.DroneId.Equals(droneId, StringComparison.OrdinalIgnoreCase) && !Mission.Workflow.IsTerminal(m.Status));

    /// <summary>Open work orders that resolve to <paramref name="crewId"/> today but to nobody after the edit.</summary>
    private static async Task<int> OrphanedWorkOrdersAsync(IGridRepository repo, IReadOnlyList<Crew> before, IReadOnlyList<Crew> after, string crewId, CancellationToken ct) =>
        (await repo.ListFindingsAsync(1000, ct)).Count(f =>
            f.Status == FindingStatus.Assigned
            && CrewRoster.Resolve(before, f.AssignedTo)?.Crew.Id == crewId
            && CrewRoster.Resolve(after, f.AssignedTo) is null);

    private static void Upsert<T>(List<T> list, T item, Predicate<T> same)
    {
        var i = list.FindIndex(same);
        if (i >= 0) list[i] = item; else list.Add(item);
    }

    private static List<CrewMember> CleanMembers(IReadOnlyList<CrewMember>? members) =>
        (members ?? []).Select(m => new CrewMember(m.Name?.Trim() ?? "", m.Role?.Trim() ?? "")).ToList();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
