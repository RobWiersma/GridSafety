namespace GridSafety.Core;

/// <summary>
/// Where one technician stands in their shift. Times are real (sped-up demo) time; <see cref="Shifts.Hours"/> turns
/// them into the simulated hours people see.
/// </summary>
/// <param name="StartedAt">When the current shift's first job set off; null when they haven't worked since their last rest.</param>
/// <param name="Worked">Drive and repair time so far this shift.</param>
/// <param name="Booked">Worked plus the rest of the jobs already lined up (through the drive back).</param>
/// <param name="RestingUntil">Set while they're off, resting after a full shift.</param>
public sealed record ShiftState(DateTimeOffset? StartedAt, TimeSpan Worked, TimeSpan Booked, DateTimeOffset? RestingUntil)
{
    public static readonly ShiftState Fresh = new(null, TimeSpan.Zero, TimeSpan.Zero, null);

    /// <summary>Time still free for new work this shift.</summary>
    public TimeSpan Left => RestingUntil is null && Booked < Shifts.Limit ? Shifts.Limit - Booked : TimeSpan.Zero;
}

/// <summary>
/// Hours of service for field crews: up to <see cref="Limit"/> of driving and repairs per shift, then a
/// <see cref="Rest"/> before the next one. Worked out from the trucks' own timestamps, like everything else in the
/// field simulation, so nothing needs a background clock. Demo time runs <see cref="DemoSpeed"/>× fast: one real
/// second is one simulated minute, so a full shift plays out in eight real minutes.
/// </summary>
public static class Shifts
{
    /// <summary>Simulated seconds per real second (matches the drive and repair times in <see cref="FieldWork"/>).</summary>
    public const double DemoSpeed = 60;

    /// <summary>Real time standing in for <paramref name="simulated"/> time.</summary>
    public static TimeSpan Sim(TimeSpan simulated) => simulated / DemoSpeed;

    /// <summary>Simulated hours that <paramref name="real"/> demo time stands for.</summary>
    public static double Hours(TimeSpan real) => Math.Round(real.TotalHours * DemoSpeed, 1);

    public const double LimitHours = 8, RestHours = 10, ClockOutUnderHours = 2;

    /// <summary>Most drive and repair time in one shift.</summary>
    public static readonly TimeSpan Limit = Sim(TimeSpan.FromHours(LimitHours));

    /// <summary>Time off between shifts. Being idle this long also starts a fresh shift.</summary>
    public static readonly TimeSpan Rest = Sim(TimeSpan.FromHours(RestHours));

    /// <summary>Back at the depot with less than this left: they clock out and rest.</summary>
    public static readonly TimeSpan ClockOutUnder = Sim(TimeSpan.FromHours(ClockOutUnderHours));

    /// <summary>Road distance is longer than the straight line; estimates use this much more, to stay on the safe side.</summary>
    public const double RoadFactor = 1.4;

    /// <summary>
    /// <paramref name="member"/>'s shift as of <paramref name="now"/>, from every truck roll they've been on (jobs sent
    /// to their whole crew count for each member).
    /// </summary>
    public static ShiftState Of(IEnumerable<FieldDispatch> history, string crewId, string member, DateTimeOffset now)
    {
        // Only the current shift counts: the jobs since the last gap long enough to be a rest.
        if (Group(history, crewId, member) is not [.., var shift]) return ShiftState.Fresh;
        var spans = Spans(shift);
        var end = spans[^1].End;
        if (now - end >= Rest) return ShiftState.Fresh; // rested since
        var booked = TimeSpan.FromTicks(spans.Sum(s => (s.End - s.Start).Ticks));
        var worked = TimeSpan.FromTicks(spans.Sum(s => (Min(now, s.End) - Min(now, s.Start)).Ticks));
        return new ShiftState(spans[0].Start, worked, booked, ClockedOut(booked, end, now) ? end + Rest : null);
    }

    /// <summary>Back at the depot with too little of the shift left to be worth another job: done until rested.</summary>
    private static bool ClockedOut(TimeSpan booked, DateTimeOffset end, DateTimeOffset now) => end <= now && Limit - booked < ClockOutUnder;

    /// <summary>
    /// Someone's truck rolls in time order, split into shifts wherever there's a gap long enough to be a rest (jobs sent to
    /// their whole crew count too).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FieldDispatch>> Group(IEnumerable<FieldDispatch> history, string crewId, string member)
    {
        var shifts = new List<List<FieldDispatch>>();
        var end = DateTimeOffset.MinValue;
        foreach (var d in history.Where(d => d.Involves(crewId, member) && d.ReturnsAt > d.DepartedAt).OrderBy(d => d.DepartedAt))
        {
            if (shifts.Count == 0 || d.DepartedAt - end >= Rest) shifts.Add([]);
            end = shifts[^1].Count == 0 ? d.ReturnsAt : Max(end, d.ReturnsAt);
            shifts[^1].Add(d);
        }
        return shifts;
    }

    /// <summary>Time on the clock as spans, merged where jobs touch (one handing straight over to the next).</summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Spans(IEnumerable<FieldDispatch> shift)
    {
        var spans = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var d in shift.OrderBy(d => d.DepartedAt))
        {
            if (spans.Count > 0 && d.DepartedAt <= spans[^1].End) spans[^1] = (spans[^1].Start, Max(spans[^1].End, d.ReturnsAt));
            else spans.Add((d.DepartedAt, d.ReturnsAt));
        }
        return spans;
    }

    /// <summary>
    /// A shift report per shift <paramref name="member"/> has worked, newest first: when it ran, hours driving and
    /// repairing, every job and how it ended, and the rest that followed. Times not yet reached (jobs lined up) are
    /// booked but not worked.
    /// </summary>
    public static IReadOnlyList<ShiftReport> Reports(IEnumerable<Finding> findings, string crewId, string member, DateTimeOffset now, int max = 20)
    {
        var byDispatch = findings.Where(f => f.Dispatch is not null).ToDictionary(f => f.Dispatch!, ReferenceEqualityComparer.Instance);
        var shifts = Group(byDispatch.Keys.Cast<FieldDispatch>(), crewId, member);
        var reports = new List<ShiftReport>();
        for (var i = shifts.Count - 1; i >= 0 && reports.Count < max; i--)
        {
            var spans = Spans(shifts[i]);
            var (start, end) = (spans[0].Start, spans[^1].End);
            var booked = TimeSpan.FromTicks(spans.Sum(s => (s.End - s.Start).Ticks));
            var worked = TimeSpan.FromTicks(spans.Sum(s => (Min(now, s.End) - Min(now, s.Start)).Ticks));
            var nextStart = i + 1 < shifts.Count ? Spans(shifts[i + 1])[0].Start : (DateTimeOffset?)null;
            var status = now < end ? ShiftStatus.Working
                : nextStart is not null || now - end >= Rest ? ShiftStatus.Done
                : ClockedOut(booked, end, now) ? ShiftStatus.Resting
                : ShiftStatus.OnCall;
            var jobs = shifts[i].Select(d => Job((Finding)byDispatch[d]!, d, now, member)).ToList();
            reports.Add(new ShiftReport(start, end, status, Hours(worked), Hours(booked), Math.Round(jobs.Sum(j => j.DriveHours), 1),
                Math.Round(jobs.Sum(j => j.RepairHours), 1), jobs.Count(j => j.Outcome == JobOutcome.Repaired),
                nextStart is { } n ? Hours(n - end) : null, LimitHours, jobs));
        }
        return reports;
    }

    internal static ShiftJob Job(Finding f, FieldDispatch d, DateTimeOffset now, string member)
    {
        // Time at the site: from arrival until the repair is done, or until they were called away.
        var leftSite = Min(d.RecalledAt ?? d.RepairedAt, d.RepairedAt);
        var onSite = leftSite > d.ArrivesAt ? leftSite - d.ArrivesAt : TimeSpan.Zero;
        var outcome = d.RecalledAt is not null ? JobOutcome.CalledBack
            : now >= d.RepairedAt ? JobOutcome.Repaired
            : now >= d.DepartedAt ? JobOutcome.InProgress
            : JobOutcome.LinedUp;
        return new ShiftJob(f.Id, f.StructureLabel, f.Type, f.Severity, d.Assignee, d.FromLabel, d.DepartedAt, d.ArrivesAt, d.ReturnsAt,
            Hours(d.ReturnsAt - d.DepartedAt - onSite), Hours(onSite), outcome, d.NextSiteLabel, d.OnRoads, d.VehicleLabel,
            d.Riders?.Where(r => !string.Equals(r.Name, member, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).ToList() is { Count: > 0 } with ? string.Join(" & ", with) : null);
    }

    /// <summary>
    /// Rough real time a job would add to someone's shift: the drive there (from <paramref name="from"/>), the repair, and
    /// the drive back to <paramref name="depot"/>, less the drive home they'd no longer make when going on from an
    /// earlier job (<paramref name="after"/>). The repair is quicker with more <paramref name="people"/> on it.
    /// </summary>
    public static TimeSpan Estimate(GeoPoint from, GeoPoint site, GeoPoint depot, int severity, FieldDispatch? after = null, int people = 1)
    {
        static TimeSpan Drive(GeoPoint a, GeoPoint b) => FieldWork.TravelTime(Geo.DistanceMeters(a, b) / 1000 * RoadFactor);
        var job = Drive(from, site) + FieldWork.RepairTime(severity, people) + Drive(site, depot);
        return after is null ? job : job - (after.ReturnsAt - after.RepairedAt);
    }

    /// <summary>A whole crew's shift is as tight as its most worked member's.</summary>
    public static ShiftState Combine(IEnumerable<ShiftState> members)
    {
        var all = members.ToList();
        if (all.Count == 0) return ShiftState.Fresh;
        return new ShiftState(all.Min(s => s.StartedAt), all.Max(s => s.Worked), all.Max(s => s.Booked), all.Max(s => s.RestingUntil));
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

public enum ShiftStatus { Working, OnCall, Resting, Done }

public enum JobOutcome { Repaired, CalledBack, InProgress, LinedUp }

/// <summary>
/// One job in a shift report, with its time split into driving and repairing (simulated hours), the truck, and who else
/// was in it (<see cref="With"/>).
/// </summary>
public sealed record ShiftJob(string FindingId, string StructureLabel, FindingType Type, int Severity, string Assignee, string FromLabel,
    DateTimeOffset DepartedAt, DateTimeOffset ArrivesAt, DateTimeOffset EndsAt, double DriveHours, double RepairHours, JobOutcome Outcome,
    string? NextSiteLabel, bool OnRoads, string? Vehicle = null, string? With = null);

/// <summary>
/// One shift for one technician. Hours are simulated: <see cref="WorkedHours"/> up to now, <see cref="BookedHours"/>
/// including jobs lined up. <see cref="RestHours"/> is the break before their next shift, once there is one.
/// </summary>
public sealed record ShiftReport(DateTimeOffset StartedAt, DateTimeOffset EndsAt, ShiftStatus Status, double WorkedHours, double BookedHours,
    double DriveHours, double RepairHours, int Repaired, double? RestHours, double LimitHours, IReadOnlyList<ShiftJob> Jobs)
{
    /// <summary>The hours worked at the technician's rate when the shift started.</summary>
    public double LaborCost { get; init; }
}

/// <summary>
/// Hours of service to check candidates against: every truck roll so far, the time, and how bad the defect is (repairs
/// take longer on worse ones). Shifts are worked out once per person and shared by copies made with <c>with</c>, so a
/// list of findings can reuse one.
/// </summary>
public sealed record FieldHours(IReadOnlyList<FieldDispatch> History, DateTimeOffset Now, int Severity = 3)
{
    private readonly Dictionary<(string, string), ShiftState> _cache = [];

    public ShiftState Of(Crew c, CrewMember? m) => m is not null ? Member(c.Id, m.Name) : Shifts.Combine(c.Members.Select(x => Member(c.Id, x.Name)));

    private ShiftState Member(string crewId, string name)
    {
        var key = (crewId, name.ToLowerInvariant());
        lock (_cache)
        {
            if (!_cache.TryGetValue(key, out var s)) _cache[key] = s = Shifts.Of(History, crewId, name, Now);
            return s;
        }
    }
}
