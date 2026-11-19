using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>A fleet drone with its live state and how much it has flown (from the last 1,000 missions).</summary>
public sealed record DroneResourceView(
    DroneAsset Drone, DroneState? Live, string? ActiveMissionId, string? ActiveMissionName,
    int Missions, double FlightHours, double DistanceMeters, DateTimeOffset? LastFlight);

/// <summary>A flight crew with the missions flown by its pilots.</summary>
public sealed record FlightCrewView(FlightCrew Crew, int Missions, int ActiveMissions, DateTimeOffset? LastFlight);

/// <summary>A drone's flight log: every mission it flew, newest first, with totals.</summary>
public sealed record DroneReportView(DroneAsset Drone, FlightReport Report, DateTimeOffset Now);

/// <summary>A remote pilot's flight log (missions where they were pilot in command).</summary>
public sealed record PilotReportView(string CrewId, string CrewName, string Member, string Role, FlightReport Report, DateTimeOffset Now);

/// <summary>A truck's work report: every trip it made and who was in it.</summary>
public sealed record VehicleReportView(Vehicle Vehicle, string KindLabel, string? DepotName, WorkReport Work, DateTimeOffset Now);

/// <summary>A ground crew's work report: every job its people were on, broken down per member.</summary>
public sealed record CrewWorkReportView(string CrewId, string CrewName, string Specialty, string? DepotName, WorkReport Work, DateTimeOffset Now);

/// <summary>A yard's work report: every truck roll out of it, broken down per truck, with how many people depart from it.</summary>
public sealed record DepotReportView(Depot Depot, int Trucks, int People, WorkReport Work, DateTimeOffset Now);

/// <summary>A flight crew's flight log: missions flown by any of its pilots.</summary>
public sealed record FlightCrewReportView(string CrewId, string CrewName, string HomeBase, FlightReport Report, DateTimeOffset Now);

/// <summary>A truck with the trip it's on now (if any), who's in it, and how much it has been used.</summary>
public sealed record VehicleView(Vehicle Vehicle, string KindLabel, FieldDispatch? Trip, FieldPhase? Phase, int Jobs, DateTimeOffset? LastOut);

public sealed record ResourcesView(IReadOnlyList<DroneResourceView> Drones, IReadOnlyList<FlightCrewView> FlightCrews, IReadOnlyList<CrewView> GroundCrews,
    IReadOnlyList<Depot> Depots, IReadOnlyList<VehicleView> Vehicles);

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
        secured.MapPut("/resources/vehicles/{id}", PutVehicle);
        secured.MapDelete("/resources/vehicles/{id}", DeleteVehicle);

        // Reports: a drone's and a pilot's flight log, a technician's shifts.
        secured.MapGet("/resources/drones/{id}/report", GetDroneReport);
        secured.MapGet("/resources/flight-crews/{crewId}/members/{member}/report", GetPilotReport);
        secured.MapGet("/resources/ground-crews/{crewId}/members/{member}/report", GetShiftReports);

        // Work reports for each kind of resource as a whole.
        secured.MapGet("/resources/vehicles/{id}/report", GetVehicleReport);
        secured.MapGet("/resources/ground-crews/{crewId}/report", GetCrewWorkReport);
        secured.MapGet("/resources/flight-crews/{crewId}/report", GetFlightCrewReport);
        secured.MapGet("/resources/depots/{id}/report", GetDepotReport);
    }

    private static async Task<Results<Ok<DroneReportView>, NotFound>> GetDroneReport(
        string id, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var catalog = await resources.GetAsync(ct);
        if (catalog.Drones.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } drone) return TypedResults.NotFound();
        var missions = (await repo.ListMissionsAsync(1000, ct)).Where(m => m.DroneId.Equals(drone.Id, StringComparison.OrdinalIgnoreCase));
        return TypedResults.Ok(new DroneReportView(drone, FlightReports.Of(missions, circuits.Find), clock.GetUtcNow()));
    }

    private static async Task<Results<Ok<PilotReportView>, NotFound>> GetPilotReport(
        string crewId, string member, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var catalog = await resources.GetAsync(ct);
        if (catalog.FlightCrews.FirstOrDefault(c => c.Id == crewId) is not { } crew
            || crew.Members.FirstOrDefault(m => string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase)) is not { } m)
            return TypedResults.NotFound();
        // Missions record the pilot in command only; visual observers get an empty log.
        var missions = m.Role == FlightCrew.PilotRole ? (await repo.ListMissionsAsync(1000, ct)).Where(x => FlownBy(x, m)) : [];
        return TypedResults.Ok(new PilotReportView(crew.Id, crew.Name, m.Name, m.Role, FlightReports.Of(missions, circuits.Find), clock.GetUtcNow()));
    }

    private static async Task<Results<Ok<VehicleReportView>, NotFound>> GetVehicleReport(
        string id, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        if (catalog.FindVehicle(id) is not { } v) return TypedResults.NotFound();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var work = WorkReports.Of(findings, d => d.VehicleId == v.Id, d => WorkReports.PeopleOf(d), now);
        return TypedResults.Ok(new VehicleReportView(v, Vehicle.KindLabel(v.Kind), catalog.FindDepot(v.DepotId)?.Name, work, now));
    }

    private static async Task<Results<Ok<CrewWorkReportView>, NotFound>> GetCrewWorkReport(
        string crewId, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        if (catalog.GroundCrews.FirstOrDefault(c => c.Id == crewId) is not { } crew) return TypedResults.NotFound();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        // Per member: every job they were on, whether they did the repair or rode along.
        var work = WorkReports.Of(findings, d => d.Involves(crew.Id, null),
            d => crew.Members.Where(m => d.Involves(crew.Id, m.Name)).Select(m => m.Name),
            now, [.. crew.Members.Select(m => (m.Name, (string?)m.Role))]);
        return TypedResults.Ok(new CrewWorkReportView(crew.Id, crew.Name, crew.Specialty, catalog.FindDepot(crew.DepotId)?.Name, work, now));
    }

    private static async Task<Results<Ok<FlightCrewReportView>, NotFound>> GetFlightCrewReport(
        string crewId, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var catalog = await resources.GetAsync(ct);
        if (catalog.FlightCrews.FirstOrDefault(c => c.Id == crewId) is not { } crew) return TypedResults.NotFound();
        var missions = (await repo.ListMissionsAsync(1000, ct)).Where(m => crew.Pilots.Any(p => FlownBy(m, p)));
        return TypedResults.Ok(new FlightCrewReportView(crew.Id, crew.Name, crew.HomeBase, FlightReports.Of(missions, circuits.Find), clock.GetUtcNow()));
    }

    private static async Task<Results<Ok<DepotReportView>, NotFound>> GetDepotReport(
        string id, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        if (catalog.FindDepot(id) is not { } depot) return TypedResults.NotFound();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var trucks = catalog.Vehicles.Where(v => v.DepotId == depot.Id).ToList();
        var people = catalog.GroundCrews.Sum(c => c.Members.Count(m => c.DepotOf(m) == depot.Id));
        // Per truck; rolls from before trucks were tracked go under the person sent.
        var work = WorkReports.Of(findings, d => d.DepotId == depot.Id, d => [d.VehicleId ?? d.Member ?? d.Assignee], now,
            [.. trucks.Select(v => (v.Id, (string?)$"{Vehicle.KindLabel(v.Kind)}, {v.Seats} seats{(v.Status == VehicleStatus.OutOfService ? ", out of service" : "")}"))]);
        return TypedResults.Ok(new DepotReportView(depot, trucks.Count, people, work, now));
    }

    private static async Task<Ok<ResourcesView>> ListResources(IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
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

        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var open = OpenWorkOrders(findings, catalog.GroundCrews);
        var hours = new FieldHours(FieldWork.History(findings), now);
        var active = FieldWork.Active(findings, now);
        var groundCrews = catalog.GroundCrews
            .Select(c => new CrewView(c, open.GetValueOrDefault(c.Id), [.. active.Where(d => d.Involves(c.Id, null))], MemberShifts(c, hours))).ToList();
        var history = FieldWork.History(findings);
        var vehicles = catalog.Vehicles.Select(v =>
        {
            var trip = Fleet.CurrentTrip(v, active, now);
            var used = history.Where(d => d.VehicleId == v.Id).ToList();
            return new VehicleView(v, Vehicle.KindLabel(v.Kind), trip, trip?.PhaseAt(now), used.Count, used.Max(d => (DateTimeOffset?)d.DepartedAt));
        }).ToList();
        return TypedResults.Ok(new ResourcesView(drones, flightCrews, groundCrews, catalog.Depots, vehicles));
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
            var crew = body with { Id = id.Trim(), Name = body.Name?.Trim()!, HomeBase = body.HomeBase?.Trim()!, Members = [.. CleanMembers(body.Members).Select(m => m with { Skills = null, DepotId = null })] };
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
            var crew = body with { Id = id.Trim(), Name = body.Name?.Trim()!, Specialty = body.Specialty?.Trim()!, Members = CleanMembers(body.Members), Handles = body.Handles?.Distinct().ToList()!, DepotId = Clean(body.DepotId) };
            if (ResourceCatalog.Validate(crew) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
            if (catalog.ValidateDepots(crew) is { Count: > 0 } depotErrors) return TypedResults.ValidationProblem(depotErrors);
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

    private static Task<IResult> PutVehicle(string id, Vehicle body, HttpContext ctx, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            var vehicle = body with { Id = id.Trim().ToUpperInvariant(), DepotId = body.DepotId?.Trim()!, Notes = Clean(body.Notes) };
            if (catalog.Validate(vehicle) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
            if (catalog.FindVehicle(vehicle.Id) is { } old && (old.Status != vehicle.Status || old.DepotId != vehicle.DepotId)
                && await TruckOutAsync(repo, old, clock, ct))
                return TypedResults.Conflict($"{old.Id} is out on a job. Wait until it's back at the yard to move it or take it out of service.");
            Upsert(catalog.Vehicles, vehicle, v => v.Id.Equals(vehicle.Id, StringComparison.OrdinalIgnoreCase));
            return TypedResults.Ok(vehicle);
        }, ct);

    private static Task<IResult> DeleteVehicle(string id, HttpContext ctx, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            if (catalog.FindVehicle(id) is not { } vehicle) return TypedResults.NotFound();
            if (await TruckOutAsync(repo, vehicle, clock, ct)) return TypedResults.Conflict($"{vehicle.Id} is out on a job. Wait until it's back to remove it.");
            catalog.Vehicles.Remove(vehicle);
            return TypedResults.NoContent();
        }, ct);

    private static async Task<bool> TruckOutAsync(IGridRepository repo, Vehicle v, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return Fleet.IsOut(v, FieldWork.Active(await ListSettledFindingsAsync(repo, now, ct), now));
    }

    /// <summary>Supervisor-only read-modify-write of the catalog, retried when another edit lands first.</summary>
    private static async Task<IResult> EditAsync(HttpContext ctx, IResourceStore resources, Func<ResourceCatalog, Task<IResult>> edit, CancellationToken ct)
    {
        if (!User(ctx).IsSupervisor)
            return TypedResults.Problem("Only supervisors can change resources.", statusCode: StatusCodes.Status403Forbidden);
        for (var attempt = 0; ; attempt++)
        {
            var catalog = await resources.GetAsync(ct);
            var result = await edit(catalog);
            if (result is not (Ok<DroneAsset> or Ok<FlightCrew> or Ok<Crew> or Ok<Vehicle> or NoContent)) return result;
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
        (members ?? []).Select(m => new CrewMember(m.Name?.Trim() ?? "", m.Role?.Trim() ?? "", m.Skills?.Distinct().ToList(), Clean(m.DepotId))).ToList();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
