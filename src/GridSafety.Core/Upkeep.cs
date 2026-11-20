namespace GridSafety.Core;

/// <summary>What a truck or drone is doing about its upkeep right now. Anything but <see cref="Ready"/> keeps it at the yard.</summary>
public enum UpkeepState { Ready, Refueling, Charging, Servicing, InShop, OutOfService }

public enum MaintenanceKind { Refuel, Service, Inspection, BatterySwap, BatteryReplaced, Repair }

/// <summary>
/// A repair or service a supervisor logged (the simulation works out refuels, charging and routine service by itself).
/// A repair keeps the asset in the shop from <see cref="At"/> until <see cref="Until"/>, or until it's marked back when
/// <see cref="Until"/> is null; a logged service resets the service counter.
/// </summary>
public sealed record MaintenanceRecord(string Id, string AssetId, MaintenanceKind Kind, DateTimeOffset At, DateTimeOffset? Until, string? Note, string? By,
    double? Cost = null);

/// <summary>One line of an asset's maintenance log, simulated or logged (<see cref="RecordId"/> set).</summary>
public sealed record MaintenanceEvent(string AssetId, MaintenanceKind Kind, DateTimeOffset At, DateTimeOffset? Until, string Detail,
    string? RecordId = null, string? By = null, double? Gallons = null, double? Cost = null);

/// <summary>Upkeep spend, by kind.</summary>
public sealed record UpkeepCosts(double Fuel, double Service, double Batteries, double Repairs)
{
    public double Total => Costs.Money(Fuel + Service + Batteries + Repairs);
}

/// <summary>A truck's fuel, service and shop state, worked out from the trips it has made and what was logged.</summary>
public sealed record TruckUpkeep(double FuelPct, double TankGallons, double RangeMiles, double MilesDriven, double MilesToService,
    UpkeepState State, DateTimeOffset? BusyUntil, string? Note, IReadOnlyList<MaintenanceEvent> Events)
{
    public bool Available => State == UpkeepState.Ready;
    public UpkeepCosts Costs => Upkeep.CostsOf(Events);
}

/// <summary>One of a drone's battery packs. <see cref="HealthPct"/> falls with each full charge cycle.</summary>
public sealed record BatteryPack(string Id, double Cycles, double HealthPct, double ChargePct, DateTimeOffset? ReadyAt, bool InUse);

/// <summary>A drone's batteries, inspection and shop state, worked out from the missions it has flown and what was logged.</summary>
public sealed record DroneUpkeep(IReadOnlyList<BatteryPack> Packs, int ReadyPacks, DateTimeOffset? NextReadyAt, double FlightHours,
    double HoursToInspection, UpkeepState State, DateTimeOffset? BusyUntil, string? Note, IReadOnlyList<MaintenanceEvent> Events)
{
    public bool CanFly => State == UpkeepState.Ready;
    public UpkeepCosts Costs => Upkeep.CostsOf(Events);
}

/// <summary>
/// Simulated upkeep for trucks and drones, worked out from history like the rest of the field simulation (no background
/// clock). Trucks burn fuel along their routes (simulated time: refuel and service times are sped up like everything
/// else in the field); drones fly in real time, so their charging and inspections take real minutes. Service intervals
/// are scaled down so a demo sees them come round. Starting fuel, mileage and battery wear vary per asset (from its id)
/// so the fleet doesn't all fill up at once.
/// </summary>
public static class Upkeep
{
    public const double GasPrice = Costs.GasPerGallon; // $/gal
    public const double RefuelUnderPct = 50, ServiceEveryMiles = 300;
    public static readonly TimeSpan RefuelTime = Shifts.Sim(TimeSpan.FromMinutes(15)), ServiceTime = Shifts.Sim(TimeSpan.FromHours(2));

    public const int PacksPerDrone = 4, DemoDronePacks = 8;

    /// <summary>Battery packs a drone has: the on-demand demo drone carries a bigger kit so demos can fly back to back.</summary>
    public static int PacksFor(DroneAsset d) => d.Id.Equals(Simulation.DemoFlight.DroneId, StringComparison.OrdinalIgnoreCase) ? DemoDronePacks : PacksPerDrone;
    public const double ReadyChargePct = 90, HealthPerCycle = 1.0, ReplaceUnderHealthPct = 70, InspectEveryHours = 2;
    public static readonly TimeSpan FullCharge = TimeSpan.FromMinutes(40), SwapTime = TimeSpan.FromMinutes(1), InspectionTime = TimeSpan.FromMinutes(10);

    /// <summary>Tank size (gallons) and fuel economy (miles per gallon) for each kind of truck.</summary>
    public static (double Tank, double Mpg) Specs(VehicleKind k) => k switch
    {
        VehicleKind.BucketTruck => (50, 8),
        VehicleKind.LineTruck => (60, 7),
        _ => (26, 18),
    };

    /// <summary>Road miles a job's drives cover (out, and back to the yard or on to the next job's start).</summary>
    public static double Miles(FieldDispatch d) => (PathMeters(d.Route) + PathMeters(d.ReturnRoute)) / 1609.344;

    public static TruckUpkeep Truck(Vehicle v, IEnumerable<FieldDispatch> history, IEnumerable<MaintenanceRecord> records, DateTimeOffset now)
    {
        var (tank, mpg) = Specs(v.Kind);
        var fuel = tank * (0.55 + 0.4 * Seed(v.Id, 1)); // gallons
        var sinceService = ServiceEveryMiles * 0.85 * Seed(v.Id, 2);
        var driven = 0.0;
        var logged = records.Where(r => r.AssetId.Equals(v.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.At).ToList();
        var events = new List<MaintenanceEvent>();
        var services = new Queue<MaintenanceRecord>(logged.Where(r => r.Kind == MaintenanceKind.Service && r.At <= now));

        foreach (var d in history.Where(d => d.VehicleId == v.Id && d.DepartedAt <= now).OrderBy(d => d.DepartedAt))
        {
            while (services.TryPeek(out var s) && s.At <= d.DepartedAt) { services.Dequeue(); sinceService = 0; }
            var miles = Miles(d);
            if (now < d.ReturnsAt)
            {
                // On the road: count the share of the drive done so far.
                var share = Math.Clamp((now - d.DepartedAt) / (d.ReturnsAt - d.DepartedAt), 0, 1);
                fuel -= miles * share / mpg;
                sinceService += miles * share;
                driven += miles * share;
                continue;
            }
            fuel -= miles / mpg;
            sinceService += miles;
            driven += miles;
            if (d.NextFindingId is not null) continue; // went straight on to the next job

            // Back at the yard: in for service when it's due (and filled up there), otherwise fill up when under half.
            if (sinceService >= ServiceEveryMiles)
            {
                events.Add(new(v.Id, MaintenanceKind.Service, d.ReturnsAt, d.ReturnsAt + ServiceTime, $"Routine service at {Math.Round(sinceService)} mi: oil, brakes, boom inspection",
                    Cost: Costs.TruckService));
                sinceService = 0;
            }
            if (fuel < tank * RefuelUnderPct / 100 || events is [.., { Kind: MaintenanceKind.Service } last] && last.At == d.ReturnsAt)
            {
                var gallons = Math.Round(tank - Math.Max(0, fuel), 1);
                events.Add(new(v.Id, MaintenanceKind.Refuel, d.ReturnsAt, d.ReturnsAt + RefuelTime, $"Filled up at {d.DepotName}: {gallons} gal",
                    Gallons: gallons, Cost: Math.Round(gallons * GasPrice, 2)));
                fuel = tank;
            }
        }
        if (services.Count > 0) sinceService = 0; // serviced since its last trip
        events.AddRange(logged.Where(r => r.At <= now).Select(r => Logged(r, Costs.TruckRepairDefault, Costs.TruckServiceLogged)));

        var (state, until, note) = Now(events, now);
        if (v.Status == VehicleStatus.OutOfService) (state, until, note) = (UpkeepState.OutOfService, null, v.Notes);
        fuel = Math.Clamp(fuel, 0, tank);
        return new TruckUpkeep(Math.Round(fuel / tank * 100, 1), tank, Math.Round(fuel * mpg), Math.Round(driven, 1),
            Math.Round(Math.Max(0, ServiceEveryMiles - sinceService)), state, until, note, [.. events.OrderByDescending(e => e.At)]);
    }

    public static DroneUpkeep Drone(DroneAsset drone, IEnumerable<Mission> missions, IEnumerable<MaintenanceRecord> records, DateTimeOffset now)
    {
        var packs = Enumerable.Range(0, PacksFor(drone))
            .Select(i => new Pack($"{(char)('A' + i)}", 18 * Seed(drone.Id, 10 + i)))
            .ToList();
        var hours = InspectEveryHours * 0.85 * Seed(drone.Id, 3);
        var flown = 0.0;
        var logged = records.Where(r => r.AssetId.Equals(drone.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.At).ToList();
        var events = new List<MaintenanceEvent>();
        var services = new Queue<MaintenanceRecord>(logged.Where(r => r.Kind == MaintenanceKind.Service && r.At <= now));

        foreach (var m in missions.Where(m => m.DroneId.Equals(drone.Id, StringComparison.OrdinalIgnoreCase) && m.StartedAt is { } s && s <= now)
                     .OrderBy(m => m.StartedAt))
        {
            var start = m.StartedAt!.Value;
            while (services.TryPeek(out var s) && s.At <= start) { services.Dequeue(); hours = 0; }
            // The fullest pack goes in (the least worn of equals).
            var pack = packs.Where(p => !p.InUse).OrderByDescending(p => Math.Round(p.ChargeAt(start))).ThenBy(p => p.Cycles).FirstOrDefault();
            if (pack is null) continue;
            var startCharge = pack.ChargeAt(start);
            var landed = m.EndedAt ?? (Mission.Workflow.IsTerminal(m.Status) ? m.UpdatedAt : (DateTimeOffset?)null);
            if (landed is null || landed > now)
            {
                pack.InUse = true;
                pack.LastCharge = m.BatteryPct ?? startCharge;
                flown += FlightReports.Hours(m);
                continue;
            }

            var end = Math.Clamp(m.BatteryPct ?? startCharge - 40, 0, startCharge);
            pack.Cycles += (startCharge - end) / 100;
            var air = FlightReports.Hours(m); // time in the air, not the faster demo playback
            flown += air;
            hours += air;
            if (100 - pack.Cycles * HealthPerCycle < ReplaceUnderHealthPct)
            {
                events.Add(new(drone.Id, MaintenanceKind.BatteryReplaced, landed.Value, null,
                    $"Pack {pack.Id} retired at {Math.Round(100 - pack.Cycles * HealthPerCycle)}% health after {Math.Round(pack.Cycles)} cycles; a new pack goes in its place",
                    Cost: Costs.BatteryPack));
                pack.Cycles = 0;
                pack.LastCharge = 100;
                pack.ChargingFrom = null;
            }
            else
            {
                pack.LastCharge = end;
                pack.ChargingFrom = landed.Value + SwapTime;
                events.Add(new(drone.Id, MaintenanceKind.BatterySwap, landed.Value, pack.ReadyAt,
                    $"Pack {pack.Id} off at {Math.Round(end)}% and on the charger"));
            }
            if (hours >= InspectEveryHours)
            {
                events.Add(new(drone.Id, MaintenanceKind.Inspection, landed.Value, landed.Value + InspectionTime,
                    $"Routine inspection after {hours:0.0} flight hours: props, motors, gimbal, firmware", Cost: Costs.DroneInspection));
                hours = 0;
            }
        }
        if (services.Count > 0) hours = 0;
        events.AddRange(logged.Where(r => r.At <= now).Select(r => Logged(r, Costs.DroneRepairDefault, Costs.DroneServiceLogged)));

        var views = packs.Select(p =>
        {
            var charge = p.InUse ? p.LastCharge : p.ChargeAt(now);
            return new BatteryPack(p.Id, Math.Round(p.Cycles, 1), Math.Round(100 - p.Cycles * HealthPerCycle, 1), Math.Round(charge),
                !p.InUse && charge < ReadyChargePct ? p.ReadyAt : null, p.InUse);
        }).ToList();
        var ready = views.Count(p => !p.InUse && p.ChargePct >= ReadyChargePct);
        var nextReady = views.Where(p => p.ReadyAt is not null).Min(p => p.ReadyAt);

        var (state, until, note) = Now(events, now);
        if (drone.Status == DroneAssetStatus.Retired) (state, until, note) = (UpkeepState.OutOfService, null, drone.Notes);
        else if (drone.Status == DroneAssetStatus.Maintenance) (state, until, note) = (UpkeepState.InShop, null, drone.Notes ?? "In maintenance");
        else if (state == UpkeepState.Ready && ready == 0 && views.Any(p => !p.InUse))
            (state, until, note) = (UpkeepState.Charging, nextReady, "Every battery pack is charging");
        return new DroneUpkeep(views, ready, nextReady, Math.Round(flown, 2), Math.Round(Math.Max(0, InspectEveryHours - hours), 2),
            state, until, note, [.. events.OrderByDescending(e => e.At)]);
    }

    /// <summary>A plain-language reason an asset can't go out, e.g. "T-071 is refueling until 3:41 PM".</summary>
    public static string Why(string id, UpkeepState state, DateTimeOffset? until, string? note)
    {
        var what = state switch
        {
            UpkeepState.Refueling => "refueling",
            UpkeepState.Charging => "charging its batteries",
            UpkeepState.Servicing => "in for routine service",
            UpkeepState.InShop => "in the shop",
            UpkeepState.OutOfService => "out of service",
            _ => "ready",
        };
        return $"{id} is {what}{(until is { } u ? $" until {u:HH:mm} UTC" : "")}{(note is { Length: > 0 } ? $" ({note})" : "")}.";
    }

    /// <summary>Whatever covers <paramref name="now"/>: a logged repair first, then service, then refueling.</summary>
    private static (UpkeepState, DateTimeOffset?, string?) Now(List<MaintenanceEvent> events, DateTimeOffset now)
    {
        bool Covers(MaintenanceEvent e) => e.At <= now && (e.Until is null ? e.RecordId is not null : now < e.Until);
        if (events.LastOrDefault(e => e.Kind == MaintenanceKind.Repair && Covers(e)) is { } repair) return (UpkeepState.InShop, repair.Until, repair.Detail);
        if (events.LastOrDefault(e => e.Kind is MaintenanceKind.Service or MaintenanceKind.Inspection && e.RecordId is null && Covers(e)) is { } service)
            return (UpkeepState.Servicing, service.Until, null);
        if (events.LastOrDefault(e => e.Kind == MaintenanceKind.Refuel && Covers(e)) is { } refuel) return (UpkeepState.Refueling, refuel.Until, null);
        return (UpkeepState.Ready, null, null);
    }

    private static MaintenanceEvent Logged(MaintenanceRecord r, double repairCost, double serviceCost) => new(r.AssetId, r.Kind, r.At,
        r.Kind == MaintenanceKind.Repair ? r.Until : r.At,
        r.Note is { Length: > 0 } n ? n : r.Kind == MaintenanceKind.Repair ? "Repair" : "Service logged", r.Id, r.By,
        Cost: r.Cost ?? (r.Kind == MaintenanceKind.Repair ? repairCost : serviceCost));

    /// <summary>What an asset's upkeep cost: fuel bought, and service, inspections, batteries and repairs.</summary>
    public static UpkeepCosts CostsOf(IEnumerable<MaintenanceEvent> events)
    {
        var all = events.ToList();
        return new UpkeepCosts(
            Costs.Money(all.Where(e => e.Kind == MaintenanceKind.Refuel).Sum(e => e.Cost ?? 0)),
            Costs.Money(all.Where(e => e.Kind is MaintenanceKind.Service or MaintenanceKind.Inspection).Sum(e => e.Cost ?? 0)),
            Costs.Money(all.Where(e => e.Kind == MaintenanceKind.BatteryReplaced).Sum(e => e.Cost ?? 0)),
            Costs.Money(all.Where(e => e.Kind == MaintenanceKind.Repair).Sum(e => e.Cost ?? 0)));
    }

    private static double PathMeters(IReadOnlyList<GeoPoint> path)
    {
        var m = 0.0;
        for (var i = 1; i < path.Count; i++) m += Geo.DistanceMeters(path[i - 1], path[i]);
        return m;
    }

    /// <summary>A stable 0–1 value per asset id and purpose (string.GetHashCode changes between runs).</summary>
    private static double Seed(string id, int salt)
    {
        var h = 2166136261u ^ (uint)salt;
        foreach (var c in id.ToUpperInvariant()) h = (h ^ c) * 16777619u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h % 10_000) / 10_000.0;
    }

    private sealed class Pack(string id, double cycles)
    {
        public string Id { get; } = id;
        public double Cycles { get; set; } = cycles;
        public double LastCharge { get; set; } = 100;
        public DateTimeOffset? ChargingFrom { get; set; }
        public bool InUse { get; set; }

        public DateTimeOffset? ReadyAt => ChargingFrom is { } from ? from + FullCharge * ((100 - LastCharge) / 100) : null;

        public double ChargeAt(DateTimeOffset t) => ChargingFrom is not { } from || t <= from ? LastCharge
            : Math.Min(100, LastCharge + (t - from) / FullCharge * 100);
    }
}
