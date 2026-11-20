using System.Globalization;
using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>A fleet drone with its live state and how much it has flown (from the last 1,000 missions).</summary>
public sealed record DroneResourceView(
    DroneAsset Drone, DroneState? Live, string? ActiveMissionId, string? ActiveMissionName,
    int Missions, double FlightHours, double DistanceMeters, DateTimeOffset? LastFlight, DroneUpkeep Upkeep);

/// <summary>A flight crew with the missions flown by its pilots.</summary>
public sealed record FlightCrewView(FlightCrew Crew, int Missions, int ActiveMissions, DateTimeOffset? LastFlight);

/// <summary>A drone's flight log: every mission it flew, newest first, with totals.</summary>
public sealed record DroneReportView(DroneAsset Drone, FlightReport Report, DroneUpkeep Upkeep, DateTimeOffset Now);

/// <summary>A remote pilot's flight log (missions where they were pilot in command).</summary>
public sealed record PilotReportView(string CrewId, string CrewName, string Member, string Role, FlightReport Report, DateTimeOffset Now);

/// <summary>A truck's work report: every trip it made and who was in it.</summary>
public sealed record VehicleReportView(Vehicle Vehicle, string KindLabel, string? DepotName, WorkReport Work, TruckUpkeep Upkeep, DateTimeOffset Now);

/// <summary>A ground crew's work report: every job its people were on, broken down per member.</summary>
public sealed record CrewWorkReportView(string CrewId, string CrewName, string Specialty, string? DepotName, WorkReport Work, DateTimeOffset Now);

/// <summary>A yard's work report: every truck roll out of it, broken down per truck, with how many people depart from it.</summary>
public sealed record DepotReportView(Depot Depot, int Trucks, int People, WorkReport Work, UpkeepCosts TruckUpkeep, DateTimeOffset Now);

/// <summary>A flight crew's flight log: missions flown by any of its pilots.</summary>
public sealed record FlightCrewReportView(string CrewId, string CrewName, string HomeBase, FlightReport Report, DateTimeOffset Now);

/// <summary>The whole operation day by day (last 1,000 missions and findings).</summary>
public sealed record ActivityReportView(IReadOnlyList<ActivityDay> Days, RateCard Rates, DateTimeOffset Now);

/// <summary>A truck with the trip it's on now (if any), who's in it, and how much it has been used.</summary>
public sealed record VehicleView(Vehicle Vehicle, string KindLabel, FieldDispatch? Trip, FieldPhase? Phase, int Jobs, DateTimeOffset? LastOut, TruckUpkeep Upkeep);

/// <summary>
/// A repair or service to log. A repair puts the asset in the shop for <see cref="Hours"/> (simulated hours for a truck,
/// real hours for a drone), or until it's marked back when there are none; a service resets the service counter.
/// </summary>
/// <summary>One employee's edit: crew details, employee record, and own hourly rate (null: the standard rate).</summary>
public sealed record EmployeeEdit(string Name, string Role, IReadOnlyList<FindingType>? Skills, string? DepotId, EmployeeProfile? Profile, double? PayRate);

/// <summary>An hourly rate in dollars, or null to go back to the standard rate.</summary>
public sealed record PayRateRequest(double? Rate);

public sealed record MaintenanceRequest(MaintenanceKind Kind, double? Hours, string? Note, double? Cost = null);

public sealed record ResourcesView(IReadOnlyList<DroneResourceView> Drones, IReadOnlyList<FlightCrewView> FlightCrews, IReadOnlyList<CrewView> GroundCrews,
    IReadOnlyList<Depot> Depots, IReadOnlyList<VehicleView> Vehicles);

/// <summary>Drones, flight crews and ground crews. Everyone can see them; supervisors add, edit and remove them.</summary>
public static partial class Endpoints
{
    /// <summary>The read-only reports, for signed-in users and (redacted, through the guest group) the public demo.</summary>
    private static void MapReports(RouteGroupBuilder group)
    {
        // A drone's and a pilot's flight log, a technician's shifts.
        group.MapGet("/resources/drones/{id}/report", GetDroneReport);
        group.MapGet("/resources/flight-crews/{crewId}/members/{member}/report", GetPilotReport);
        group.MapGet("/resources/ground-crews/{crewId}/members/{member}/report", GetShiftReports);

        // Work reports for each kind of resource as a whole, and the whole operation.
        group.MapGet("/resources/vehicles/{id}/report", GetVehicleReport);
        group.MapGet("/resources/ground-crews/{crewId}/report", GetCrewWorkReport);
        group.MapGet("/resources/flight-crews/{crewId}/report", GetFlightCrewReport);
        group.MapGet("/resources/depots/{id}/report", GetDepotReport);
        group.MapGet("/reports/activity", GetActivityReport);
        group.MapGet("/reports/rates", () => TypedResults.Ok(Costs.Card));
    }

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
        MapReports(secured);

        // Maintenance: supervisors log repairs and services; refuels and charging are simulated.
        secured.MapPost("/resources/drones/{id}/maintenance", (string id, MaintenanceRequest body, HttpContext ctx, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
            LogMaintenance("drones", id, body, ctx, repo, resources, clock, ct));
        secured.MapPost("/resources/vehicles/{id}/maintenance", (string id, MaintenanceRequest body, HttpContext ctx, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
            LogMaintenance("vehicles", id, body, ctx, repo, resources, clock, ct));
        secured.MapPost("/resources/maintenance/{recordId}/done", FinishRepair);

        // One employee: their details, employee record and own pay rate. Supervisors only.
        secured.MapPut("/resources/ground-crews/{crewId}/members/{member}", (string crewId, string member, EmployeeEdit body, HttpContext ctx, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
            PutEmployee("ground-crews", crewId, member, body, ctx, repo, resources, clock, ct));
        secured.MapPut("/resources/flight-crews/{crewId}/members/{member}", (string crewId, string member, EmployeeEdit body, HttpContext ctx, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
            PutEmployee("flight-crews", crewId, member, body, ctx, repo, resources, clock, ct));

        // Pay: a person's own hourly rate, in place of the standard one. Supervisors only.
        secured.MapPut("/resources/ground-crews/{crewId}/members/{member}/pay", (string crewId, string member, PayRateRequest body, HttpContext ctx, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
            PutPayRate("ground-crews", crewId, member, body, ctx, resources, clock, ct));
        secured.MapPut("/resources/flight-crews/{crewId}/members/{member}/pay", (string crewId, string member, PayRateRequest body, HttpContext ctx, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
            PutPayRate("flight-crews", crewId, member, body, ctx, resources, clock, ct));
    }

    private static async Task<Results<Ok<DroneReportView>, NotFound>> GetDroneReport(
        string id, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var catalog = await resources.GetAsync(ct);
        if (catalog.Drones.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } drone) return TypedResults.NotFound();
        var missions = (await repo.ListMissionsAsync(1000, ct)).Where(m => m.DroneId.Equals(drone.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var now = clock.GetUtcNow();
        return TypedResults.Ok(new DroneReportView(drone, FlightReports.Of(missions, circuits.Find, rates: PayRates.Of(catalog)), Upkeep.Drone(drone, missions, catalog.Maintenance, now), now));
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
        return TypedResults.Ok(new PilotReportView(crew.Id, crew.Name, m.Name, m.Role, FlightReports.Of(missions, circuits.Find, rates: PayRates.Of(catalog)), clock.GetUtcNow()));
    }

    private static async Task<Results<Ok<VehicleReportView>, NotFound>> GetVehicleReport(
        string id, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        if (catalog.FindVehicle(id) is not { } v) return TypedResults.NotFound();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var work = WorkReports.Of(findings, d => d.VehicleId == v.Id, d => WorkReports.PeopleOf(d), now, rates: PayRates.Of(catalog));
        return TypedResults.Ok(new VehicleReportView(v, Vehicle.KindLabel(v.Kind), catalog.FindDepot(v.DepotId)?.Name, work,
            Upkeep.Truck(v, FieldWork.History(findings), catalog.Maintenance, now), now));
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
            now, [.. crew.Members.Select(m => (m.Name, (string?)m.Role))], rates: PayRates.Of(catalog));
        return TypedResults.Ok(new CrewWorkReportView(crew.Id, crew.Name, crew.Specialty, catalog.FindDepot(crew.DepotId)?.Name, work, now));
    }

    private static async Task<Results<Ok<FlightCrewReportView>, NotFound>> GetFlightCrewReport(
        string crewId, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var catalog = await resources.GetAsync(ct);
        if (catalog.FlightCrews.FirstOrDefault(c => c.Id == crewId) is not { } crew) return TypedResults.NotFound();
        var missions = (await repo.ListMissionsAsync(1000, ct)).Where(m => crew.Pilots.Any(p => FlownBy(m, p)));
        return TypedResults.Ok(new FlightCrewReportView(crew.Id, crew.Name, crew.HomeBase, FlightReports.Of(missions, circuits.Find, rates: PayRates.Of(catalog)), clock.GetUtcNow()));
    }

    private static async Task<Ok<ActivityReportView>> GetActivityReport(IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var missions = await repo.ListMissionsAsync(1000, ct);
        var catalog = await resources.GetAsync(ct);
        var history = FieldWork.History(findings);
        var upkeep = catalog.Vehicles.SelectMany(v => Upkeep.Truck(v, history, catalog.Maintenance, now).Events)
            .Concat(catalog.Drones.SelectMany(d => Upkeep.Drone(d, missions, catalog.Maintenance, now).Events));
        return TypedResults.Ok(new ActivityReportView(ActivityReports.Daily(findings, missions, now, upkeep, PayRates.Of(catalog)), Costs.Card, now));
    }

    private const string NoTruck = "No truck";

    private static async Task<Results<Ok<DepotReportView>, NotFound>> GetDepotReport(
        string id, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        if (catalog.FindDepot(id) is not { } depot) return TypedResults.NotFound();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var trucks = catalog.Vehicles.Where(v => v.DepotId == depot.Id).ToList();
        var people = catalog.GroundCrews.Sum(c => c.Members.Count(m => c.DepotOf(m) == depot.Id));
        // Per truck; rolls from before trucks were tracked share one line, so the table lists only trucks.
        var untracked = findings.Any(f => f.Dispatch is { VehicleId: null } d && d.DepotId == depot.Id);
        var work = WorkReports.Of(findings, d => d.DepotId == depot.Id, d => [d.VehicleId ?? NoTruck], now,
            [.. trucks.Select(v => (v.Id, (string?)$"{Vehicle.KindLabel(v.Kind)}, {v.Seats} seats{(v.Status == VehicleStatus.OutOfService ? ", out of service" : "")}")),
                .. untracked ? [(NoTruck, (string?)"sent before trucks were tracked")] : Array.Empty<(string, string?)>()], rates: PayRates.Of(catalog));
        var history = FieldWork.History(findings);
        var upkeep = Upkeep.CostsOf(trucks.SelectMany(v => Upkeep.Truck(v, history, catalog.Maintenance, now).Events));
        return TypedResults.Ok(new DepotReportView(depot, trucks.Count, people, work, upkeep, now));
    }

    private static async Task<Ok<ResourcesView>> ListResources(IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        var missions = await repo.ListMissionsAsync(1000, ct);
        var live = DroneState.Settled(await repo.ListDronesAsync(ct), missions).ToDictionary(d => d.DroneId, StringComparer.OrdinalIgnoreCase);

        var drones = catalog.Drones.Select(d =>
        {
            var flown = missions.Where(m => m.DroneId.Equals(d.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var active = flown.FirstOrDefault(m => !Mission.Workflow.IsTerminal(m.Status));
            var hours = flown.Where(m => m.StartedAt is not null).Sum(m => ((m.EndedAt ?? m.UpdatedAt) - m.StartedAt!.Value).TotalHours);
            return new DroneResourceView(d, live.GetValueOrDefault(d.Id), active?.Id, active?.Name,
                flown.Count, Math.Round(Math.Max(0, hours), 1), flown.Sum(m => m.DistanceFlownMeters), flown.Max(m => (DateTimeOffset?)(m.StartedAt ?? m.CreatedAt)),
                Upkeep.Drone(d, flown, catalog.Maintenance, now));
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
            return new VehicleView(v, Vehicle.KindLabel(v.Kind), trip, trip?.PhaseAt(now), used.Count, used.Max(d => (DateTimeOffset?)d.DepartedAt),
                Upkeep.Truck(v, history, catalog.Maintenance, now));
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
            var crew = body with { Id = id.Trim(), Name = body.Name?.Trim()!, HomeBase = body.HomeBase?.Trim()!, Members = KeepPersonal(catalog.FlightCrews.FirstOrDefault(c => c.Id == id.Trim())?.Members, CleanMembers(body.Members)) };
            return Task.FromResult(SaveFlightCrew(catalog, crew));
        }, ct);

    /// <summary>Validates and stores a flight crew (members carry no skills or depots).</summary>
    private static IResult SaveFlightCrew(ResourceCatalog catalog, FlightCrew crew)
    {
        crew = crew with { Members = [.. crew.Members.Select(m => m with { Skills = null, DepotId = null })] };
        if (ResourceCatalog.Validate(crew) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
        if (ProfileErrors(crew.Members) is { Count: > 0 } profileErrors) return TypedResults.ValidationProblem(profileErrors);
        Upsert(catalog.FlightCrews, crew, c => c.Id == crew.Id);
        return TypedResults.Ok(crew);
    }

    private static Task<IResult> DeleteFlightCrew(string id, HttpContext ctx, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, catalog =>
            Task.FromResult<IResult>(catalog.FlightCrews.RemoveAll(c => c.Id == id) > 0 ? TypedResults.NoContent() : TypedResults.NotFound()), ct);

    private static Task<IResult> PutGroundCrew(string id, Crew body, HttpContext ctx, IGridRepository repo, IResourceStore resources, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            var crew = body with { Id = id.Trim(), Name = body.Name?.Trim()!, Specialty = body.Specialty?.Trim()!, Members = KeepPersonal(catalog.GroundCrews.FirstOrDefault(c => c.Id == id.Trim())?.Members, CleanMembers(body.Members)), Handles = body.Handles?.Distinct().ToList()!, DepotId = Clean(body.DepotId) };
            return await SaveGroundCrewAsync(catalog, crew, repo, ct);
        }, ct);

    /// <summary>Validates and stores a ground crew, refusing an edit that would orphan open work orders.</summary>
    private static async Task<IResult> SaveGroundCrewAsync(ResourceCatalog catalog, Crew crew, IGridRepository repo, CancellationToken ct)
    {
        if (ResourceCatalog.Validate(crew) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
        if (ProfileErrors(crew.Members) is { Count: > 0 } profileErrors) return TypedResults.ValidationProblem(profileErrors);
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
    }

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

    private static Task<IResult> LogMaintenance(string kind, string id, MaintenanceRequest body, HttpContext ctx, IGridRepository repo,
        IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            if (body.Kind is not (MaintenanceKind.Repair or MaintenanceKind.Service))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Log a repair or a service; refuels and charging happen by themselves."] });
            if (body.Hours is < 0 or > 24 * 30)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["hours"] = ["Hours in the shop: 0 to 720."] });
            if (body.Cost is < 0 or > 1_000_000)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cost"] = ["Cost: $0 to $1,000,000."] });
            if (body.Note?.Length > 200)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["note"] = ["Keep the note under 200 characters."] });

            var now = clock.GetUtcNow();
            string assetId;
            TimeSpan? shop = body.Hours is > 0 and var h ? TimeSpan.FromHours(h) : null;
            if (kind == "drones")
            {
                if (catalog.FindDrone(id) is not { } drone) return TypedResults.NotFound();
                if (await ActiveMissionAsync(repo, drone.Id, ct) is { } m)
                    return TypedResults.Conflict($"{drone.Id} is on mission {m.Name ?? m.Id}. Land it first.");
                assetId = drone.Id;
            }
            else
            {
                if (catalog.FindVehicle(id) is not { } truck) return TypedResults.NotFound();
                if (await TruckOutAsync(repo, truck, clock, ct))
                    return TypedResults.Conflict($"{truck.Id} is out on a job. Log it once it's back at the yard.");
                assetId = truck.Id;
                shop = shop is { } s ? Shifts.Sim(s) : null; // truck time runs at demo speed
            }
            if (catalog.Maintenance.Any(r => r.AssetId == assetId && r.Kind == MaintenanceKind.Repair && r.At <= now && (r.Until is null || now < r.Until))
                && body.Kind == MaintenanceKind.Repair)
                return TypedResults.Conflict($"{assetId} is already in the shop. Mark it back first.");

            var record = new MaintenanceRecord(Guid.NewGuid().ToString("N")[..12], assetId, body.Kind, now,
                body.Kind == MaintenanceKind.Repair ? now + shop : now, Clean(body.Note), User(ctx).DisplayName, body.Cost);
            catalog.Maintenance.Add(record);
            if (catalog.Maintenance.Count > ResourceCatalog.MaxMaintenanceRecords)
                catalog.Maintenance.RemoveRange(0, catalog.Maintenance.Count - ResourceCatalog.MaxMaintenanceRecords);
            return TypedResults.Ok(record);
        }, ct);

    /// <summary>Back from the shop: ends an open repair now.</summary>
    private static Task<IResult> FinishRepair(string recordId, HttpContext ctx, IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
        EditAsync(ctx, resources, catalog =>
        {
            var now = clock.GetUtcNow();
            var i = catalog.Maintenance.FindIndex(r => r.Id == recordId);
            if (i < 0) return Task.FromResult<IResult>(TypedResults.NotFound());
            var r = catalog.Maintenance[i];
            if (r.Kind != MaintenanceKind.Repair || (r.Until is { } u && u <= now))
                return Task.FromResult<IResult>(TypedResults.Conflict("That repair is already over."));
            catalog.Maintenance[i] = r with { Until = now };
            return Task.FromResult<IResult>(TypedResults.Ok(catalog.Maintenance[i]));
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
            if (result is not (Ok<DroneAsset> or Ok<FlightCrew> or Ok<Crew> or Ok<Vehicle> or Ok<MaintenanceRecord> or Ok<CrewMember> or NoContent)) return result;
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
        (members ?? []).Select(m => new CrewMember(m.Name?.Trim() ?? "", m.Role?.Trim() ?? "", m.Skills?.Distinct().ToList(), Clean(m.DepotId),
            Profile: m.Profile?.Cleaned())).ToList();

    private static Dictionary<string, string[]> ProfileErrors(IEnumerable<CrewMember> members) =>
        members.Select(m => Employees.Validate(m.Profile)).FirstOrDefault(e => e.Count > 0) ?? [];

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Editing a whole crew keeps each person's own rate (pay is set per person: <see cref="PutEmployee"/>, <see cref="PutPayRate"/>)
    /// and their employee record when the edit doesn't send one.
    /// </summary>
    private static List<CrewMember> KeepPersonal(IReadOnlyList<CrewMember>? before, List<CrewMember> after) =>
        [.. after.Select(m => before?.FirstOrDefault(b => b.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)) is { } was
            ? m with { PayRate = was.PayRate, PayHistory = was.PayHistory, Profile = m.Profile ?? was.Profile }
            : m with { PayRate = null, PayHistory = null })];

    private static Dictionary<string, string[]>? PayError(double? rate) =>
        rate is { } r && (double.IsNaN(r) || r < Costs.MinPayRate || r > Costs.MaxPayRate)
            ? new() { ["payRate"] = [string.Create(CultureInfo.InvariantCulture, $"An hourly rate is between ${Costs.MinPayRate:0} and ${Costs.MaxPayRate:0}.")] }
            : null;

    /// <summary>
    /// Edits one employee: name, role, (ground crews) skills and depot, their employee record, and their own pay rate (null:
    /// the standard rate). Supervisors only. Goes through the same checks as editing their crew, so a rename that would
    /// orphan open work orders is refused. A new rate applies from now on; earlier work keeps the rate it was done at.
    /// </summary>
    private static Task<IResult> PutEmployee(string kind, string crewId, string member, EmployeeEdit body, HttpContext ctx, IGridRepository repo,
        IResourceStore resources, TimeProvider clock, CancellationToken ct) =>
        EditAsync(ctx, resources, async catalog =>
        {
            if (PayError(body.PayRate) is { } payError) return TypedResults.ValidationProblem(payError);
            if (Employees.Validate(body.Profile?.Cleaned()) is { Count: > 0 } profileErrors) return TypedResults.ValidationProblem(profileErrors);
            var now = clock.GetUtcNow();
            CrewMember updated = null!;
            List<CrewMember>? Replace(IReadOnlyList<CrewMember> members)
            {
                if (members.FirstOrDefault(m => m.Name.Equals(member, StringComparison.OrdinalIgnoreCase)) is not { } was) return null;
                updated = PayRates.Change(was, body.PayRate is { } rate ? Costs.Money(rate) : null, now) with
                {
                    Name = body.Name?.Trim() ?? "", Role = body.Role?.Trim() ?? "", Skills = body.Skills?.Distinct().ToList(),
                    DepotId = Clean(body.DepotId), Profile = body.Profile?.Cleaned(),
                };
                return [.. members.Select(m => ReferenceEquals(m, was) ? updated : m)];
            }

            if (kind == "ground-crews")
            {
                if (catalog.GroundCrews.FirstOrDefault(c => c.Id == crewId) is not { } crew || Replace(crew.Members) is not { } members)
                    return TypedResults.NotFound();
                var saved = await SaveGroundCrewAsync(catalog, crew with { Members = members }, repo, ct);
                return saved is Ok<Crew> ? TypedResults.Ok(updated) : saved;
            }
            if (catalog.FlightCrews.FirstOrDefault(c => c.Id == crewId) is not { } flight || Replace(flight.Members) is not { } flyers)
                return TypedResults.NotFound();
            var result = SaveFlightCrew(catalog, flight with { Members = flyers });
            return result is Ok<FlightCrew> ? TypedResults.Ok(updated with { Skills = null, DepotId = null }) : result;
        }, ct);

    /// <summary>
    /// Sets (or, with no rate, clears) one person's own hourly rate. Supervisors only. Ground crews: a technician's rate.
    /// Flight crews: a pilot's or an observer's share of the flight crew rate.
    /// </summary>
    private static Task<IResult> PutPayRate(string kind, string crewId, string member, PayRateRequest body, HttpContext ctx, IResourceStore resources,
        TimeProvider clock, CancellationToken ct) =>
        EditAsync(ctx, resources, catalog =>
        {
            if (PayError(body.Rate) is { } payError) return Task.FromResult<IResult>(TypedResults.ValidationProblem(payError));
            var rate = body.Rate is { } set ? Costs.Money(set) : (double?)null;
            CrewMember? updated = null;
            List<CrewMember> Set(IReadOnlyList<CrewMember> members) =>
                [.. members.Select(m => m.Name.Equals(member, StringComparison.OrdinalIgnoreCase) ? updated = PayRates.Change(m, rate, clock.GetUtcNow()) : m)];
            if (kind == "ground-crews" && catalog.GroundCrews.FindIndex(c => c.Id == crewId) is var g and >= 0)
                catalog.GroundCrews[g] = catalog.GroundCrews[g] with { Members = Set(catalog.GroundCrews[g].Members) };
            else if (kind == "flight-crews" && catalog.FlightCrews.FindIndex(c => c.Id == crewId) is var f and >= 0)
                catalog.FlightCrews[f] = catalog.FlightCrews[f] with { Members = Set(catalog.FlightCrews[f].Members) };
            return Task.FromResult<IResult>(updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated));
        }, ct);
}
