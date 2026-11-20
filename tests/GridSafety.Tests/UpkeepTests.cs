using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

public class UpkeepTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
    private static readonly Vehicle Bucket = new("T-071", VehicleKind.BucketTruck, 2, "richmond");
    private static readonly DroneAsset Quad = new("DRN-01", "Inspection quad", "Concord yard");
    private static readonly GeoPoint Yard = new(37.94, -122.35);

    /// <summary>A round trip from the yard <paramref name="km"/> east and back, taking <paramref name="real"/> real time.</summary>
    private static FieldDispatch Trip(DateTimeOffset left, double km, TimeSpan real, string? vehicle = "T-071")
    {
        var site = new GeoPoint(Yard.Lat, Yard.Lon + km / 88.0);
        return new FieldDispatch
        {
            Assignee = "Lenny Leonardo (Sector 7G)", CrewId = "sector-7g", Member = "Lenny Leonardo", DepotId = "richmond", DepotName = "Richmond yard",
            Depot = Yard, Site = site, SiteLabel = "SA-1", From = Yard, FromLabel = "Richmond yard", Route = [Yard, site], ReturnRoute = [site, Yard],
            DepartedAt = left, ArrivesAt = left + real / 3, RepairedAt = left + real * 2 / 3, ReturnsAt = left + real, VehicleId = vehicle,
        };
    }

    private static List<FieldDispatch> Trips(int n, double km) =>
        [.. Enumerable.Range(0, n).Select(i => Trip(T0 + TimeSpan.FromMinutes(10 * i), km, TimeSpan.FromMinutes(5)))];

    private static Mission Flight(int i, TimeSpan every, TimeSpan length, double endPct = 20) => new()
    {
        Id = $"m{i}", CircuitId = "c1", DroneId = Quad.Id, Pilot = "Edna", Status = MissionStatus.Completed, CreatedAt = T0 + every * i,
        StartedAt = T0 + every * i, EndedAt = T0 + every * i + length, UpdatedAt = T0 + every * i + length, BatteryPct = endPct,
    };

    [Fact]
    public void A_truck_burns_fuel_by_road_miles_and_fills_up_back_at_the_yard_when_under_half()
    {
        var trips = Trips(2, 80); // ~100 road miles a trip, a quarter of a bucket truck's range
        MaintenanceRecord[] serviced = [new("r0", "T-071", MaintenanceKind.Service, T0 - TimeSpan.FromHours(1), T0, null, "dana")]; // so only fuel is in play
        var before = Upkeep.Truck(Bucket, [], serviced, T0);
        var after = Upkeep.Truck(Bucket, trips, serviced, trips[^1].ReturnsAt + TimeSpan.FromHours(1));

        var miles = trips.Sum(Upkeep.Miles);
        Assert.InRange(miles, 190, 210);
        Assert.Equal(Math.Round(miles, 1), after.MilesDriven);
        Assert.DoesNotContain(after.Events, e => e.Kind == MaintenanceKind.Service && e.RecordId is null);
        var refuel = Assert.Single(after.Events, e => e.Kind == MaintenanceKind.Refuel); // under half after two trips at the latest
        Assert.True(refuel.Gallons > 25);
        Assert.Equal(refuel.Gallons!.Value * Upkeep.GasPrice, refuel.Cost!.Value, 1);
        Assert.True(after.FuelPct > before.FuelPct - 50); // filled up since

        // Filling up takes the truck off the board for a while.
        var during = Upkeep.Truck(Bucket, trips, serviced, refuel.At + TimeSpan.FromSeconds(1));
        Assert.Equal((UpkeepState.Refueling, refuel.At + Upkeep.RefuelTime), (during.State, during.BusyUntil));
        Assert.False(during.Available);
    }

    [Fact]
    public void Service_comes_due_by_miles_and_resets_the_counter()
    {
        var trips = Trips(4, 80); // ~400 miles: more than a full service interval
        var now = trips[^1].ReturnsAt + TimeSpan.FromHours(1);
        var upkeep = Upkeep.Truck(Bucket, trips, [], now);

        var service = Assert.Single(upkeep.Events, e => e.Kind == MaintenanceKind.Service);
        Assert.Contains(upkeep.Events, e => e.Kind == MaintenanceKind.Refuel && e.At == service.At); // filled up while in
        Assert.InRange(upkeep.MilesToService, 0, Upkeep.ServiceEveryMiles);
        Assert.Equal(Costs.TruckService, service.Cost);
        Assert.Equal(Costs.TruckService, upkeep.Costs.Service);
        Assert.Equal(upkeep.Events.Where(e => e.Kind == MaintenanceKind.Refuel).Sum(e => e.Cost!.Value), upkeep.Costs.Fuel, 2);
        Assert.Equal(UpkeepState.Servicing, Upkeep.Truck(Bucket, trips, [], service.At + TimeSpan.FromSeconds(1)).State);

        // A service logged by hand resets it too.
        var logged = new MaintenanceRecord("r1", "T-071", MaintenanceKind.Service, now, now, "Tires", "dana");
        Assert.Equal(Upkeep.ServiceEveryMiles, Upkeep.Truck(Bucket, trips, [logged], now + TimeSpan.FromSeconds(1)).MilesToService);
    }

    [Fact]
    public void A_logged_repair_keeps_it_in_the_shop_until_its_time_is_up_or_it_is_marked_back()
    {
        var open = new MaintenanceRecord("r1", "t-071", MaintenanceKind.Repair, T0, null, "Hydraulic leak", "dana");
        var shop = Upkeep.Truck(Bucket, [], [open], T0 + TimeSpan.FromDays(3));
        Assert.Equal((UpkeepState.InShop, "Hydraulic leak"), (shop.State, shop.Note));
        Assert.Contains("in the shop", Upkeep.Why("T-071", shop.State, shop.BusyUntil, shop.Note));

        Assert.Equal(Costs.TruckRepairDefault, shop.Costs.Repairs); // no cost logged: the default
        Assert.Equal(380, Upkeep.Truck(Bucket, [], [open with { Cost = 380 }], T0 + TimeSpan.FromDays(3)).Costs.Repairs);

        var back = open with { Until = T0 + TimeSpan.FromHours(1) };
        Assert.True(Upkeep.Truck(Bucket, [], [back], T0 + TimeSpan.FromHours(2)).Available);
        Assert.Equal(UpkeepState.OutOfService, Upkeep.Truck(Bucket with { Status = VehicleStatus.OutOfService }, [], [], T0).State);
    }

    [Fact]
    public void A_drone_flies_on_its_fullest_pack_and_waits_when_every_pack_is_charging()
    {
        var every = TimeSpan.FromMinutes(6);
        var flights = Enumerable.Range(0, 4).Select(i => Flight(i, every, TimeSpan.FromMinutes(5))).ToList();
        var now = flights[^1].EndedAt!.Value + TimeSpan.FromSeconds(30);

        var upkeep = Upkeep.Drone(Quad, flights, [], now);

        Assert.Equal(4, upkeep.Packs.Select(p => p.Id).Distinct().Count());
        Assert.Equal(0, upkeep.ReadyPacks);
        Assert.Equal(UpkeepState.Charging, upkeep.State);
        Assert.False(upkeep.CanFly);
        // The first pack landed at 20%: a minute to swap, then 80% of a full charge.
        var firstBack = flights[0].EndedAt!.Value + Upkeep.SwapTime + Upkeep.FullCharge * 0.8;
        Assert.Equal(firstBack, upkeep.BusyUntil);
        Assert.Equal(4, upkeep.Events.Count(e => e.Kind == MaintenanceKind.BatterySwap));

        var later = Upkeep.Drone(Quad, flights, [], firstBack + TimeSpan.FromSeconds(1));
        Assert.True(later.CanFly);
        Assert.Equal(1, later.ReadyPacks);
    }

    [Fact]
    public void Flight_hours_bring_an_inspection_round()
    {
        var flights = Enumerable.Range(0, 3).Select(i => Flight(i, TimeSpan.FromHours(2), TimeSpan.FromHours(1), endPct: 50)).ToList();
        var upkeep = Upkeep.Drone(Quad, flights, [], T0 + TimeSpan.FromDays(1));

        var inspection = Assert.Single(upkeep.Events, e => e.Kind == MaintenanceKind.Inspection);
        Assert.Equal(3, upkeep.FlightHours);
        Assert.Equal(UpkeepState.Servicing, Upkeep.Drone(Quad, flights, [], inspection.At + TimeSpan.FromMinutes(1)).State);
        Assert.Equal(UpkeepState.InShop, Upkeep.Drone(Quad with { Status = DroneAssetStatus.Maintenance }, [], [], T0).State);
    }

    [Fact]
    public void Worn_packs_are_replaced_once_they_fall_under_seventy_percent_health()
    {
        var flights = Enumerable.Range(0, 160).Select(i => Flight(i, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), endPct: 5)).ToList();
        var upkeep = Upkeep.Drone(Quad, flights, [], T0 + TimeSpan.FromDays(10));

        Assert.Contains(upkeep.Events, e => e.Kind == MaintenanceKind.BatteryReplaced);
        Assert.Equal(upkeep.Events.Count(e => e.Kind == MaintenanceKind.BatteryReplaced) * Costs.BatteryPack, upkeep.Costs.Batteries);
        Assert.All(upkeep.Packs, p => Assert.True(p.HealthPct >= Upkeep.ReplaceUnderHealthPct));
    }
}
