using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

public class VehicleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Richmond = CrewRoster.Depots.Single(d => d.Id == "richmond").Location;
    private static readonly IReadOnlyList<Vehicle> Trucks = Fleet.Default(CrewRoster.Depots);

    private static Finding Finding(string id, GeoPoint at, FindingType type = FindingType.ConductorDamage) => new()
    {
        Id = id, MissionId = "m1", CircuitId = "c1", StructureId = id, StructureLabel = id.ToUpperInvariant(),
        Type = type, Severity = 3, Location = at, CreatedAt = T0, Status = FindingStatus.Triaged,
    };

    private static GeoPoint East(GeoPoint p, double km) => new(p.Lat, p.Lon + km / 88.0);

    private static AssignCandidate Offer(Finding f, string member, IReadOnlyList<FieldDispatch> active, IReadOnlyList<Vehicle>? trucks = null) =>
        CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, f.Type, f.Location, active, new FieldHours(active, T0), trucks ?? Trucks)
            .Single(c => c.Member?.Name == member);

    private static async Task<FieldDispatch> Send(Finding f, AssignCandidate who, DateTimeOffset now, Finding? previous = null)
    {
        var d = (await FieldWork.PlanAsync(who, f, previous, new FakeRouteSource(), now))!;
        f.MoveTo(FindingStatus.Assigned, now, "dana", assignee: who.Label);
        return f.Dispatch = d;
    }

    [Fact]
    public void Every_yard_has_trucks_with_unique_ids()
    {
        Assert.All(CrewRoster.Depots, d => Assert.True(Trucks.Count(v => v.DepotId == d.Id) >= 2));
        Assert.Equal(Trucks.Count, Trucks.Select(v => v.Id).Distinct().Count());
        Assert.Equal(Trucks, ResourceCatalog.Default().Vehicles);
    }

    [Fact]
    public void Older_catalogs_get_the_demo_fleet_once()
    {
        var old = new ResourceCatalog { SeedVersion = 2, Depots = [.. CrewRoster.Depots], GroundCrews = [.. CrewRoster.All] };
        old.Normalize();
        Assert.Equal(Trucks.Count, old.Vehicles.Count);

        old.Vehicles.RemoveAt(0); // a supervisor retired one: it stays gone
        old.Normalize();
        Assert.Equal(Trucks.Count - 1, old.Vehicles.Count);
    }

    [Fact]
    public async Task A_technician_takes_the_smallest_free_truck_and_someone_from_the_same_yard_rides_along()
    {
        var f = Finding("sa-1", East(Richmond, 10));
        var lenny = Offer(f, "Lenny Leonardo", []);
        Assert.Equal("T-071", lenny.Vehicle!.Id); // Richmond's 2-seat bucket truck, not the 3-seat line truck
        Assert.Equal("Marjorie Bouvay", lenny.Partner!.Member!.Name); // the only other technician at the Richmond yard

        var d = await Send(f, lenny, T0);
        Assert.Equal("T-071", d.VehicleId);
        Assert.Equal([new Rider("sector-7g", "Lenny Leonardo"), new Rider("hot-stick", "Marjorie Bouvay")], d.Riders);
        Assert.Equal(FieldWork.RepairTime(3, people: 2), d.RepairedAt - d.ArrivesAt); // two pairs of hands

        // She's on the clock and tied up for the whole trip, though the work order is Lenny's.
        IReadOnlyList<FieldDispatch> active = [d];
        Assert.Equal(d.ReturnsAt, FieldWork.BusyUntil(active, "hot-stick", "Marjorie Bouvay"));
        Assert.Equal(d.ReturnsAt, FieldWork.BusyUntil(active, "hot-stick", null));
        Assert.True(Shifts.Of(active, "hot-stick", "Marjorie Bouvay", d.ReturnsAt).Worked > TimeSpan.Zero);
        Assert.NotEqual("T-071", Fleet.FreeAt(Trucks, "richmond", active)?.Id);
    }

    [Fact]
    public async Task Anyone_in_the_truck_can_take_a_job_nearby_so_it_goes_on_instead_of_sending_another_truck()
    {
        var first = Finding("sa-1", East(Richmond, 10));
        var d1 = await Send(first, Offer(first, "Lenny Leonardo", []), T0);

        // A hot spot 3 km on: Lenny can't fix it, but Marjorie, riding with him, can.
        var hot = Finding("sa-2", East(Richmond, 13), FindingType.ThermalHotspot);
        var marjorie = Offer(hot, "Marjorie Bouvay", [d1]);
        Assert.True(marjorie.Available);
        Assert.Same(d1, marjorie.After);
        Assert.Null(marjorie.Vehicle); // no second truck

        var d2 = await Send(hot, marjorie, T0 + TimeSpan.FromSeconds(10), previous: first);
        Assert.Equal("sa-2", first.Dispatch!.NextFindingId);
        Assert.Equal("T-071", d2.VehicleId);
        Assert.Equal("Marjorie Bouvay", d2.Member);
        Assert.Equal(d1.Riders, d2.Riders);
        Assert.Equal(d1.Site, d2.From);
        Assert.True(first.Dispatch.SameTrip(d2));
    }

    [Fact]
    public async Task A_trucks_work_report_counts_one_trip_two_jobs_and_both_people()
    {
        var first = Finding("sa-1", East(Richmond, 10));
        var d1 = await Send(first, Offer(first, "Lenny Leonardo", []), T0);
        var hot = Finding("sa-2", East(Richmond, 13), FindingType.ThermalHotspot);
        await Send(hot, Offer(hot, "Marjorie Bouvay", [d1]), T0 + TimeSpan.FromSeconds(10), previous: first);
        var end = hot.Dispatch!.ReturnsAt + TimeSpan.FromMinutes(1);
        FieldWork.Settle(first, end);
        FieldWork.Settle(hot, end);

        var report = WorkReports.Of([first, hot], d => d.VehicleId == "T-071", d => WorkReports.PeopleOf(d), end);

        Assert.Equal(1, report.Totals.Trips); // it went on from one job to the next
        Assert.Equal(2, report.Totals.Jobs);
        Assert.Equal(2, report.Totals.Repaired);
        Assert.Equal(["sa-2", "sa-1"], report.Jobs.Select(j => j.FindingId)); // newest first
        Assert.Equal("Lenny Leonardo & Marjorie Bouvay", report.Jobs[0].With);
        Assert.All(report.Breakdown, b => Assert.Equal(2, b.Jobs));
        var truckHours = Shifts.Hours(hot.Dispatch.ReturnsAt - first.Dispatch!.DepartedAt);
        Assert.Equal(truckHours * 2, report.Totals.PersonHours, 1);

        var day = Assert.Single(report.Days); // 10 am in California
        Assert.Equal((new DateOnly(2026, 11, 19), 1, 2, 2), (day.Day, day.Trips, day.Jobs, day.Repaired));
        Assert.Equal(report.Totals.PersonHours, day.PersonHours, 1);
    }

    [Fact]
    public async Task Without_a_free_truck_at_their_yard_nobody_can_leave_it()
    {
        var f = Finding("sa-1", East(Richmond, 10));
        Vehicle[] onlyOne = [new("T-071", VehicleKind.BucketTruck, 2, "richmond")];
        var d = await Send(f, Offer(f, "Lenny Leonardo", [], onlyOne), T0);

        Assert.Null(Fleet.FreeAt(onlyOne, "richmond", [d]));
        Assert.Equal(d.ReturnsAt, Fleet.NextBackAt(onlyOne, "richmond", [d]));

        Vehicle[] inTheShop = [new("T-071", VehicleKind.BucketTruck, 2, "richmond", VehicleStatus.OutOfService)];
        var lenny = Offer(Finding("sa-2", East(Richmond, 30)), "Lenny Leonardo", [], inTheShop);
        Assert.True(lenny.NoVehicle);
        Assert.False(lenny.Available);
        Assert.Null(lenny.VehicleBackAt);
    }

    [Fact]
    public void More_hands_make_repairs_quicker_with_diminishing_returns()
    {
        var one = FieldWork.RepairTime(3);
        var two = FieldWork.RepairTime(3, 2);
        var three = FieldWork.RepairTime(3, 3);
        Assert.InRange(two / one, 0.68, 0.70);
        Assert.InRange(three / one, 0.52, 0.53);
        Assert.True(one - two > two - three); // each extra person helps less
        Assert.Equal(one, FieldWork.RepairTime(3, 0)); // nobody counts as one
    }

    [Fact]
    public void A_whole_crew_takes_the_roomiest_truck_at_its_yard()
    {
        var f = Finding("sa-1", East(Richmond, 10));
        var crew = CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, f.Type, f.Location, [], null, Trucks)
            .First(c => c.Member is null && c.Crew.Id == "sector-7g");
        Assert.Equal(VehicleKind.LineTruck, crew.Vehicle!.Kind);
        Assert.Equal("concord", crew.Vehicle.DepotId);
        Assert.Null(crew.Partner);
    }
}
