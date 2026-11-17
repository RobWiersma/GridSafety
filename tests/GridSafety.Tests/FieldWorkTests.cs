using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

/// <summary>Routes every trip as a dog-leg: north first, then east (so it's clearly not a straight line).</summary>
public sealed class FakeRouteSource : IRouteSource
{
    public int Calls;
    public bool Fail { get; set; }

    public Task<RoadRoute?> RouteAsync(GeoPoint from, GeoPoint to, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Calls);
        if (Fail) throw new HttpRequestException("router down");
        List<GeoPoint> path = [from, new(to.Lat, from.Lon), to];
        return Task.FromResult<RoadRoute?>(new RoadRoute(path, FieldPaths.Length(path)));
    }
}

public class FieldWorkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 18, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Richmond = CrewRoster.Depots.Single(d => d.Id == "richmond").Location;

    private static Finding Finding(string id, GeoPoint at, int severity = 3, FindingType type = FindingType.ConductorDamage) => new()
    {
        Id = id, MissionId = "m1", CircuitId = "c1", StructureId = id, StructureLabel = id.ToUpperInvariant(),
        Type = type, Severity = severity, Location = at, CreatedAt = T0, Status = FindingStatus.Triaged,
    };

    private static GeoPoint East(GeoPoint p, double km) => new(p.Lat, p.Lon + km / 88.0);
    private static GeoPoint North(GeoPoint p, double km) => new(p.Lat + km / 111.2, p.Lon);

    /// <summary>Sends Lenny (Richmond yard) to <paramref name="f"/>, after <paramref name="previous"/> if given.</summary>
    private static async Task<FieldDispatch> Send(Finding f, DateTimeOffset now, Finding? previous = null, IRouteSource? routes = null)
    {
        var active = previous?.Dispatch is { } p ? new[] { p } : [];
        var lenny = CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, f.Type, f.Location, active).First(c => c.Member?.Name == "Lenny Leonardo");
        var d = (await FieldWork.PlanAsync(lenny, f, previous, routes ?? new FakeRouteSource(), now))!;
        f.MoveTo(FindingStatus.Assigned, now, "dana", assignee: lenny.Label);
        f.Dispatch = d;
        return d;
    }

    [Fact]
    public async Task A_job_drives_the_roads_out_repairs_and_drives_back()
    {
        var f = Finding("sa-1", North(East(Richmond, 6), 8));
        var d = await Send(f, T0);

        Assert.True(d.OnRoads);
        Assert.Equal(3, d.Route.Count); // the dog-leg, not a straight line
        var drive = d.ArrivesAt - d.DepartedAt;
        Assert.InRange(drive.TotalSeconds, FieldWork.TravelTime(14).TotalSeconds - 1, FieldWork.TravelTime(14).TotalSeconds + 1); // 8 km north + 6 km east, not the 10 km diagonal
        Assert.Equal(FieldWork.RepairTime(3), d.RepairedAt - d.ArrivesAt);

        Assert.Equal(FieldPhase.EnRoute, d.PhaseAt(T0));
        Assert.Equal(d.Depot, d.PositionAt(T0));
        var partWay = d.PositionAt(T0 + drive * (4.0 / 14));
        Assert.Equal(Richmond.Lon, partWay.Lon, 4); // still on the northbound road
        Assert.InRange(Geo.DistanceMeters(Richmond, partWay), 3_800, 4_200);
        Assert.Equal(d.Site, d.PositionAt(d.ArrivesAt + TimeSpan.FromSeconds(5)));
        Assert.Equal(FieldPhase.Returning, d.PhaseAt(d.RepairedAt));
        Assert.Equal(FieldPhase.Back, d.PhaseAt(d.ReturnsAt));
        Assert.False(d.IsActive(d.ReturnsAt));
    }

    [Fact]
    public async Task Without_a_road_route_the_truck_drives_straight()
    {
        var f = Finding("sa-1", East(Richmond, 10));
        var d = await Send(f, T0, routes: new FakeRouteSource { Fail = true });
        Assert.False(d.OnRoads);
        Assert.Equal([d.Depot, d.Site], d.Route);
    }

    [Fact]
    public async Task The_work_order_closes_when_the_repair_finishes_not_when_someone_looks()
    {
        var f = Finding("sa-1", East(Richmond, 10));
        var d = await Send(f, T0);
        Assert.False(FieldWork.Settle(f, d.RepairedAt - TimeSpan.FromSeconds(1)));
        Assert.True(FieldWork.Settle(f, d.ReturnsAt + TimeSpan.FromHours(1)));
        Assert.Equal(FindingStatus.Resolved, f.Status);
        Assert.Equal(d.RepairedAt, f.History[^1].At);
        Assert.Equal("Lenny Leonardo (Sector 7G Line Crew)", f.History[^1].By);
        Assert.False(FieldWork.Settle(f, d.ReturnsAt + TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task A_technician_out_nearby_goes_straight_on_to_the_next_job()
    {
        var first = Finding("sa-1", East(Richmond, 10));
        var d1 = await Send(first, T0);
        var second = Finding("sa-2", East(Richmond, 14));

        // While Lenny drives to the first job, a second one 4 km further on comes in: he's offered it, measured from the first.
        var at = T0 + TimeSpan.FromSeconds(10);
        IReadOnlyList<FieldDispatch> active = [first.Dispatch!];
        Assert.True(FieldWork.CanChain(active, d1.Assignee, second.Location));
        var offer = CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, second.Type, second.Location, active).Single(c => c.Member?.Name == "Lenny Leonardo");
        Assert.True(offer.Available);
        Assert.Equal(4, offer.DistanceKm!.Value, 0);
        Assert.Same(d1, offer.After);

        var d2 = await Send(second, at, previous: first);
        var handed = first.Dispatch!;
        Assert.Equal("sa-2", handed.NextFindingId);
        Assert.Equal(handed.RepairedAt, handed.ReturnsAt); // no drive back
        Assert.Equal(FieldPhase.MovedOn, handed.PhaseAt(handed.RepairedAt));

        Assert.Equal(d1.Site, d2.From);
        Assert.Equal(d1.RepairedAt, d2.DepartedAt);
        Assert.Equal(FieldPhase.Queued, d2.PhaseAt(at));
        Assert.Equal(d1.Site, d2.PositionAt(at));
        Assert.Equal(FieldPhase.EnRoute, d2.PhaseAt(d1.RepairedAt));
        Assert.Equal(d2.Depot, d2.PositionAt(d2.ReturnsAt));

        // He's tied up until the last job's drive home; his crew as a whole is busy too.
        IReadOnlyList<FieldDispatch> both = [handed, d2];
        Assert.Equal(d2.ReturnsAt, FieldWork.BusyUntil(both, "sector-7g", "Lenny Leonardo"));
        Assert.Equal(d2.ReturnsAt, FieldWork.BusyUntil(both, "sector-7g", null));
    }

    [Fact]
    public async Task A_truck_driving_back_turns_off_for_a_new_job_nearby()
    {
        var first = Finding("sa-1", East(Richmond, 10));
        var d1 = await Send(first, T0);
        var halfwayBack = d1.RepairedAt + (d1.ReturnsAt - d1.RepairedAt) / 2;
        var second = Finding("sa-2", North(East(Richmond, 10), 3));

        var d2 = await Send(second, halfwayBack, previous: first);
        var handed = first.Dispatch!;
        Assert.Equal(halfwayBack, handed.ReturnsAt);
        Assert.Equal(halfwayBack, d2.DepartedAt);
        Assert.Equal(handed.ReturnRoute[^1], d2.From); // from wherever he'd got to
        Assert.Equal(handed.PositionAt(halfwayBack - TimeSpan.FromMilliseconds(1)).Lat, d2.From.Lat, 3);
        Assert.StartsWith("on the way back from", d2.FromLabel);
    }

    [Fact]
    public async Task Jobs_far_away_or_too_many_wait_until_the_technician_is_free()
    {
        var first = Finding("sa-1", East(Richmond, 10));
        var d1 = await Send(first, T0);
        IReadOnlyList<FieldDispatch> active = [d1];

        var far = Finding("sa-far", East(Richmond, 40));
        Assert.False(FieldWork.CanChain(active, d1.Assignee, far.Location));
        var offer = CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, far.Type, far.Location, active).Single(c => c.Member?.Name == "Lenny Leonardo");
        Assert.False(offer.Available);
        Assert.Equal(d1.ReturnsAt, offer.BusyUntil);

        var chain = new List<Finding> { first };
        for (var i = 2; i <= FieldWork.MaxStops; i++)
        {
            var next = Finding($"sa-{i}", East(Richmond, 10 + i));
            await Send(next, T0, previous: chain[^1]);
            chain.Add(next);
        }
        var full = FieldWork.Active(chain, T0);
        Assert.False(FieldWork.CanChain(full, d1.Assignee, East(Richmond, 11)));
    }

    [Fact]
    public async Task Closing_a_work_order_early_turns_the_truck_back_along_the_road_it_came()
    {
        var f = Finding("sa-1", North(East(Richmond, 6), 8));
        var d = await Send(f, T0);
        var at = T0 + (d.ArrivesAt - T0) / 2;
        await FieldWork.RecallAsync(f, new FakeRouteSource(), at);
        f.MoveTo(FindingStatus.Resolved, at, "dana", "Fixed by another crew");

        var r = f.Dispatch!;
        Assert.Equal(at, r.RecalledAt);
        Assert.Equal(at + (at - T0), r.ReturnsAt);
        Assert.Equal(FieldPhase.Returning, r.PhaseAt(at + TimeSpan.FromSeconds(1)));
        Assert.Equal(r.Depot, r.PositionAt(r.ReturnsAt - TimeSpan.FromMilliseconds(1)), new GeoPointComparer());
        Assert.False(FieldWork.Settle(f, d.ReturnsAt));
    }

    [Fact]
    public void Osrm_responses_become_paths_from_the_exact_start_to_the_exact_end()
    {
        const string json = """
            {"code":"Ok","routes":[{"distance":26053.4,"duration":1243.5,"geometry":{"type":"LineString",
              "coordinates":[[-122.031109,37.978013],[-122.041844,37.9788],[-121.80588,37.997736],[-121.8058,38.004891]]}}]}
            """;
        GeoPoint from = new(37.9780, -122.0311), to = new(38.0049, -121.8058);
        var r = OsrmRouteSource.Parse(json, from, to)!;
        Assert.Equal(from, r.Path[0]);
        Assert.Equal(to, r.Path[^1]);
        Assert.Equal(6, r.Path.Count);
        Assert.Equal(new GeoPoint(37.9788, -122.041844), r.Path[2]);
        Assert.Null(OsrmRouteSource.Parse("""{"code":"NoRoute","routes":[]}""", from, to));
    }

    [Fact]
    public void Long_paths_are_thinned_but_keep_their_ends()
    {
        var path = Enumerable.Range(0, 1000).Select(i => new GeoPoint(37 + i * 1e-4, -122)).ToList();
        var thin = FieldPaths.Thin(path, 50);
        Assert.Equal(50, thin.Count);
        Assert.Equal(path[0], thin[0]);
        Assert.Equal(path[^1], thin[^1]);
    }

    private sealed class GeoPointComparer : IEqualityComparer<GeoPoint>
    {
        public bool Equals(GeoPoint a, GeoPoint b) => Geo.DistanceMeters(a, b) < 50;
        public int GetHashCode(GeoPoint p) => 0;
    }
}
