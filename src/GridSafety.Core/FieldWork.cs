namespace GridSafety.Core;

/// <summary>
/// Where a job stands. <see cref="Queued"/>: the technician is still on an earlier job and comes here next.
/// <see cref="MovedOn"/>: done here and driving on to their next job instead of back to the depot.
/// </summary>
public enum FieldPhase { Queued, EnRoute, OnSite, Returning, Back, MovedOn }

/// <summary>Someone in a truck: a technician of any crew (crewmates from the same yard ride together).</summary>
public sealed record Rider(string CrewId, string Name);

/// <summary>
/// A simulated truck roll for one work order: the technician (or whole crew) drives along the roads from their depot,
/// or from their previous job, to the defect, repairs it, and drives back to the depot, unless another job nearby is
/// next. Everything is fixed when the work order is assigned, so where the truck is at any moment is computed from the
/// clock, with nothing running in the background. Demo time is sped up (see <see cref="FieldWork"/>).
/// </summary>
public sealed record FieldDispatch
{
    /// <summary>The stored assignee label, e.g. "Lenny Leonardo (Sector 7G Line Crew)".</summary>
    public required string Assignee { get; init; }
    public required string CrewId { get; init; }
    /// <summary>The technician sent, or null when the whole crew went.</summary>
    public string? Member { get; init; }
    public required string DepotId { get; init; }
    public required string DepotName { get; init; }
    public required GeoPoint Depot { get; init; }
    public required GeoPoint Site { get; init; }
    /// <summary>The structure being repaired, e.g. "SA-010".</summary>
    public required string SiteLabel { get; init; }
    /// <summary>Where the truck sets off from: the depot, or the previous job's site.</summary>
    public required GeoPoint From { get; init; }
    public required string FromLabel { get; init; }
    /// <summary>The drive out, <see cref="From"/> to <see cref="Site"/>.</summary>
    public required IReadOnlyList<GeoPoint> Route { get; init; }
    /// <summary>The drive back to the depot; just the site itself when the truck goes on to another job instead.</summary>
    public required IReadOnlyList<GeoPoint> ReturnRoute { get; init; }
    /// <summary>Whether the routes follow roads (false: straight lines, when no road route could be found).</summary>
    public bool OnRoads { get; init; }
    public required DateTimeOffset DepartedAt { get; init; }
    public required DateTimeOffset ArrivesAt { get; init; }
    public required DateTimeOffset RepairedAt { get; init; }
    /// <summary>Back at the depot, or (with <see cref="NextFindingId"/>) the moment the truck left for its next job.</summary>
    public required DateTimeOffset ReturnsAt { get; init; }
    /// <summary>The work order this truck goes on to after this one.</summary>
    public string? NextFindingId { get; init; }
    public string? NextSiteLabel { get; init; }
    /// <summary>Set when the work order was closed by hand before the repair finished: the truck turned back.</summary>
    public DateTimeOffset? RecalledAt { get; init; }

    /// <summary>The truck, e.g. "T-071"; null for truck rolls from before vehicles were tracked.</summary>
    public string? VehicleId { get; init; }
    /// <summary>e.g. "T-071 · Bucket truck".</summary>
    public string? VehicleLabel { get; init; }
    /// <summary>
    /// Everyone in the truck, the technician doing this repair included; all of them are on the clock for the whole trip.
    /// Null when the whole crew went, or for truck rolls from before vehicles were tracked (just <see cref="Member"/>).
    /// </summary>
    public IReadOnlyList<Rider>? Riders { get; init; }

    public FieldPhase PhaseAt(DateTimeOffset now) =>
        now >= ReturnsAt ? (NextFindingId is null ? FieldPhase.Back : FieldPhase.MovedOn)
        : now < DepartedAt ? FieldPhase.Queued
        : RecalledAt is { } r && now >= r ? FieldPhase.Returning
        : now < ArrivesAt ? FieldPhase.EnRoute
        : now < RepairedAt ? FieldPhase.OnSite
        : FieldPhase.Returning;

    /// <summary>Ties the technician up until the truck is back at the depot or has moved on to its next job.</summary>
    public bool IsActive(DateTimeOffset now) => now < ReturnsAt;

    /// <summary>Where the truck is, moving at a steady speed along the stored route.</summary>
    public GeoPoint PositionAt(DateTimeOffset now) => PhaseAt(now) switch
    {
        FieldPhase.Queued => From,
        FieldPhase.EnRoute => FieldPaths.Along(Route, Progress(DepartedAt, ArrivesAt, now)),
        FieldPhase.OnSite => Site,
        FieldPhase.Returning => FieldPaths.Along(ReturnRoute, Progress(RecalledAt ?? RepairedAt, ReturnsAt, now)),
        FieldPhase.MovedOn => ReturnRoute[^1],
        _ => Depot,
    };

    /// <summary>Whether this job ties up <paramref name="member"/> of <paramref name="crewId"/> (null member: the crew as a whole).</summary>
    public bool Involves(string crewId, string? member) =>
        (CrewId == crewId && (Member is null || member is null || Same(Member, member)))
        || Riders?.Any(r => r.CrewId == crewId && (member is null || Same(r.Name, member))) == true;

    /// <summary>Whether <paramref name="member"/> of <paramref name="crewId"/> is in the truck.</summary>
    public bool Carries(string crewId, string member) => Riders?.Any(r => r.CrewId == crewId && Same(r.Name, member)) == true;

    /// <summary>Whether two jobs are stops on the same trip: the same truck, or (before vehicles were tracked) the same assignee.</summary>
    public bool SameTrip(FieldDispatch other) => VehicleId is not null ? VehicleId == other.VehicleId : other.VehicleId is null && IsFor(other.Assignee);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public bool IsFor(string assignee) => string.Equals(Assignee, assignee, StringComparison.OrdinalIgnoreCase);

    internal static double Progress(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now) =>
        to <= from ? 1 : Math.Clamp((now - from) / (to - from), 0, 1);
}

/// <summary>Plans, chains and settles simulated truck rolls. Demo time runs fast so a whole job plays out in a few minutes.</summary>
public static class FieldWork
{
    /// <summary>A technician already out can take another job this close (straight line) to the last one they're headed to.</summary>
    public const double AreaKm = 15;

    /// <summary>At most this many jobs lined up for one technician (the current one included).</summary>
    public const int MaxStops = 4;

    /// <summary>Drive time, sped up (a second stands for a minute, see <see cref="Shifts.DemoSpeed"/>): about 3 seconds per road km, 15 seconds to 3 minutes.</summary>
    public static TimeSpan TravelTime(double km) => TimeSpan.FromSeconds(Math.Clamp(15 + km * 3, 15, 180));

    /// <summary>
    /// Time on site, sped up: worse defects take longer, and more hands make it quicker, with diminishing returns
    /// (two people take about 70% of the time one would, three about 55%).
    /// </summary>
    public static TimeSpan RepairTime(int severity, int people = 1) =>
        TimeSpan.FromSeconds((40 + 15 * Math.Clamp(severity, 1, 5)) / Speedup(people));

    /// <summary>How much faster <paramref name="people"/> repair than one person: each extra pair of hands adds 45%.</summary>
    public static double Speedup(int people) => 1 + 0.45 * (Math.Clamp(people, 1, 6) - 1);

    /// <summary>How many people work the job: everyone in the truck, a whole crew, or the one technician sent.</summary>
    public static int People(AssignCandidate who, IReadOnlyList<Rider>? riders) =>
        riders?.Count ?? (who.Member is null ? Math.Max(1, who.Crew.Members.Count) : 1);

    public const string RepairedNote = "Repaired on site (simulated field crew).";

    /// <summary>Every truck roll on record, finished ones included (for hours worked).</summary>
    public static IReadOnlyList<FieldDispatch> History(IEnumerable<Finding> findings) => [.. findings.Select(f => f.Dispatch).OfType<FieldDispatch>()];

    /// <summary>Jobs that still tie someone up (driving, repairing, driving back, or lined up).</summary>
    public static IReadOnlyList<FieldDispatch> Active(IEnumerable<Finding> findings, DateTimeOffset now) =>
        [.. findings.Select(f => f.Dispatch).OfType<FieldDispatch>().Where(d => d.IsActive(now))];

    /// <summary>The last job lined up for exactly this assignee, if they're out.</summary>
    public static FieldDispatch? Tail(IReadOnlyList<FieldDispatch> active, string assignee) =>
        active.Where(d => d.IsFor(assignee) && d.NextFindingId is null).MaxBy(d => d.DepartedAt);

    /// <summary>
    /// The last stop lined up for the truck <paramref name="member"/> is out in, whether they're doing that repair or riding
    /// along; for a whole crew (null member), the last of the crew's own whole-crew jobs. Null when they're not out.
    /// </summary>
    public static FieldDispatch? Tail(IReadOnlyList<FieldDispatch> active, string crewId, string? member) =>
        active.Where(d => d.NextFindingId is null && (member is null ? d.Member is null && d.CrewId == crewId : d.Member is not null && d.Involves(crewId, member)))
            .MaxBy(d => d.DepartedAt);

    /// <summary>
    /// Whether the trip ending in <paramref name="tail"/> can take a job at <paramref name="site"/> next: it's in the same
    /// area as the last stop, the truck wasn't called back, and its list isn't full.
    /// </summary>
    public static bool CanChain(IReadOnlyList<FieldDispatch> active, FieldDispatch? tail, GeoPoint site) =>
        tail is { RecalledAt: null }
        && Geo.DistanceMeters(tail.Site, site) <= AreaKm * 1000
        && active.Count(d => d.SameTrip(tail)) < MaxStops;

    /// <inheritdoc cref="CanChain(IReadOnlyList{FieldDispatch}, FieldDispatch?, GeoPoint)"/>
    public static bool CanChain(IReadOnlyList<FieldDispatch> active, string assignee, GeoPoint site) => CanChain(active, Tail(active, assignee), site);

    /// <summary>When <paramref name="member"/> (or the whole crew, if null) is free again, or null when free now.</summary>
    public static DateTimeOffset? BusyUntil(IReadOnlyList<FieldDispatch> active, string crewId, string? member) =>
        active.Where(d => d.Involves(crewId, member)).Max(d => (DateTimeOffset?)d.ReturnsAt);

    /// <summary>
    /// Plans the truck roll for sending <paramref name="who"/> to <paramref name="f"/>: from their depot now, in the free
    /// truck picked for them (<see cref="AssignCandidate.Vehicle"/>) with their ride-along crewmate, or, when
    /// <paramref name="previous"/> is the last stop of the truck they're already out in, from that job's site as soon as
    /// it's done (or from wherever the truck is, if it's already driving back), with the same truck and people.
    /// <paramref name="previous"/> is changed to hand over to the new job instead of returning to the depot. Returns null
    /// when they have no depot.
    /// </summary>
    public static async Task<FieldDispatch?> PlanAsync(AssignCandidate who, Finding f, Finding? previous, IRouteSource routes, DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (who.Depot is not { } depot) return null;
        var (vehicleId, vehicleLabel) = (who.Vehicle?.Id, who.Vehicle?.Label);
        IReadOnlyList<Rider>? riders = who.Member is { } m
            ? [new(who.Crew.Id, m.Name), .. who.Partner is { Member: { } pm } p ? [new Rider(p.Crew.Id, pm.Name)] : Array.Empty<Rider>()]
            : null;
        if (previous?.Dispatch is { } chained)
        {
            // Same truck, same people; the job goes to whoever on it does the repair.
            (vehicleId, vehicleLabel) = (chained.VehicleId, chained.VehicleLabel);
            riders = chained.Riders is { } on && who.Member is { } doer
                ? [.. on, .. chained.Carries(who.Crew.Id, doer.Name) ? Array.Empty<Rider>() : [new Rider(who.Crew.Id, doer.Name)]]
                : riders;
            if (chained.VehicleId is not null || chained.Riders is not null) depot = new Depot(chained.DepotId, chained.DepotName, chained.Depot);
        }

        var (from, fromLabel, start) = (depot.Location, depot.Name, now);
        if (previous?.Dispatch is { } prev)
        {
            if (prev.PhaseAt(now) == FieldPhase.Returning)
            {
                // Already driving back: turn off towards the new job from where they are.
                var progress = FieldDispatch.Progress(prev.RecalledAt ?? prev.RepairedAt, prev.ReturnsAt, now);
                var turnAt = FieldPaths.Along(prev.ReturnRoute, progress);
                previous.Dispatch = prev with { ReturnRoute = FieldPaths.Prefix(prev.ReturnRoute, progress), ReturnsAt = now, NextFindingId = f.Id, NextSiteLabel = f.StructureLabel };
                (from, fromLabel, start) = (turnAt, $"on the way back from {prev.SiteLabel}", now);
            }
            else
            {
                previous.Dispatch = prev with { ReturnRoute = [prev.Site], ReturnsAt = prev.RepairedAt, NextFindingId = f.Id, NextSiteLabel = f.StructureLabel };
                (from, fromLabel, start) = (prev.Site, prev.SiteLabel, prev.RepairedAt);
            }
        }

        // Both legs at once: the API has a short time limit.
        var outboundTask = RouteOrStraightAsync(routes, from, f.Location, ct);
        var backTask = RouteOrStraightAsync(routes, f.Location, depot.Location, ct);
        var (outbound, back) = (await outboundTask, await backTask);
        var arrives = start + TravelTime(outbound.Meters / 1000);
        var repaired = arrives + RepairTime(f.Severity, People(who, riders));
        return new FieldDispatch
        {
            Assignee = who.Label, CrewId = who.Crew.Id, Member = who.Member?.Name,
            DepotId = depot.Id, DepotName = depot.Name, Depot = depot.Location,
            Site = f.Location, SiteLabel = f.StructureLabel, From = from, FromLabel = fromLabel,
            Route = outbound.Path, ReturnRoute = back.Path, OnRoads = outbound.OnRoads && back.OnRoads,
            DepartedAt = start, ArrivesAt = arrives, RepairedAt = repaired, ReturnsAt = repaired + TravelTime(back.Meters / 1000),
            VehicleId = vehicleId, VehicleLabel = vehicleLabel, Riders = riders,
        };
    }

    /// <summary>
    /// Closes the work order once the repair is done: the finding moves to Resolved, stamped with the time the repair
    /// finished (not the time someone happened to look). Returns true when the finding changed and needs saving.
    /// </summary>
    public static bool Settle(Finding f, DateTimeOffset now)
    {
        if (f.Dispatch is not { RecalledAt: null } d || f.Status != FindingStatus.Assigned || now < d.RepairedAt) return false;
        f.MoveTo(FindingStatus.Resolved, d.RepairedAt, d.Assignee, RepairedNote);
        return true;
    }

    /// <summary>
    /// The work order was closed by hand before the repair finished: the truck heads back to the depot from wherever it
    /// is (or, if this job was still lined up, straight from the job before). A truck with another job lined up after this
    /// one carries on to it as planned.
    /// </summary>
    public static async Task RecallAsync(Finding f, IRouteSource routes, DateTimeOffset now, CancellationToken ct = default)
    {
        if (f.Dispatch is not { RecalledAt: null, NextFindingId: null } d || now >= d.RepairedAt) return;
        switch (d.PhaseAt(now))
        {
            case FieldPhase.Queued:
                var home = await RouteOrStraightAsync(routes, d.From, d.Depot, ct);
                f.Dispatch = d with { RecalledAt = d.DepartedAt, ReturnRoute = home.Path, ReturnsAt = d.DepartedAt + TravelTime(home.Meters / 1000) };
                break;
            case FieldPhase.EnRoute:
                var progress = FieldDispatch.Progress(d.DepartedAt, d.ArrivesAt, now);
                var driven = FieldPaths.Prefix(d.Route, progress);
                f.Dispatch = d with { RecalledAt = now, ReturnRoute = [.. driven.Reverse()], ReturnsAt = now + (now - d.DepartedAt) };
                break;
            case FieldPhase.OnSite:
                f.Dispatch = d with { RecalledAt = now, ReturnsAt = now + (d.ReturnsAt - d.RepairedAt) };
                break;
        }
    }

    private static async Task<(IReadOnlyList<GeoPoint> Path, double Meters, bool OnRoads)> RouteOrStraightAsync(
        IRouteSource routes, GeoPoint from, GeoPoint to, CancellationToken ct)
    {
        try
        {
            if (await routes.RouteAsync(from, to, ct) is { Path.Count: >= 2 } r) return (r.Path, r.Meters, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException
                                   or KeyNotFoundException or IndexOutOfRangeException)
        {
            // Routing is best effort; fall back to a straight line.
        }
        return ([from, to], Geo.DistanceMeters(from, to), false);
    }
}
