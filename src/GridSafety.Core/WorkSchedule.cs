namespace GridSafety.Core;

/// <summary>
/// How important a work order is: a score, the reasons behind it, and the date the repair should be done by. Work goes
/// in order of severity first, so nothing ever jumps a more severe job; within a severity the score decides: how risky
/// the defect is, the line's voltage, how long it has waited, and whether it's past its repair-by date.
/// </summary>
public sealed record WorkPriority(double Score, IReadOnlyList<string> Reasons, DateTimeOffset? RepairBy);

/// <summary>
/// One work order on the forward schedule, in priority order (<see cref="Rank"/> 1 first): who it's planned for, from
/// which yard and in which truck, and when they'd set off and be back. <see cref="StartsNow"/> jobs can be dispatched
/// right away; the rest wait for someone to come back, rest, or a truck to free up. <see cref="Blocked"/> says why a
/// job can't be planned at all (nobody on duty is qualified, or their yards have no trucks in service).
/// </summary>
public sealed record ScheduledJob(int Rank, string FindingId, string StructureLabel, string CircuitId, FindingType Type, int Severity,
    double Score, IReadOnlyList<string> Reasons, DateTimeOffset FoundAt, DateTimeOffset? RepairBy,
    string? Assignee, string? CrewId, string? Member, string? DepotName, string? VehicleId,
    DateTimeOffset? StartsAt, DateTimeOffset? RepairedAt, DateTimeOffset? BackAt, bool StartsNow, bool Late, string? Blocked = null);

/// <summary>Ranks open work orders by importance and plans them forward onto technicians, shifts and trucks.</summary>
public static class WorkSchedule
{
    /// <summary>How long after it's found a repair should be done, by severity (null: no deadline, done as time allows).</summary>
    public static TimeSpan? RepairWindow(int severity) => severity switch
    {
        >= 5 => TimeSpan.FromDays(1),
        4 => TimeSpan.FromDays(7),
        3 => TimeSpan.FromDays(30),
        _ => null,
    };

    /// <summary>Waiting adds a point an hour, up to this many: an old job edges ahead of a new one of the same kind.</summary>
    public const double MaxWaitPoints = 24;

    /// <summary>Scores a work order. <paramref name="voltageKv"/> is its line's voltage, when known.</summary>
    public static WorkPriority Priority(Finding f, int? voltageKv, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(f);
        var reasons = new List<string>();
        double score = f.Severity switch { >= 5 => 100, 4 => 60, 3 => 30, 2 => 12, _ => 5 };
        reasons.Add(f.Severity switch
        {
            >= 5 => "Critical",
            4 => "High severity",
            3 => "Moderate severity",
            2 => "Low severity",
            _ => "Informational",
        });

        var (risk, why) = f.Type switch
        {
            FindingType.ThermalHotspot => (12, "failure risk"),
            FindingType.ConductorDamage => (12, "outage risk"),
            FindingType.VegetationEncroachment => (10, "fire risk"),
            FindingType.DamagedInsulator => (6, null),
            FindingType.CrossarmDamage => (6, null),
            _ => (3, (string?)null),
        };
        score += risk;
        if (why is not null) reasons.Add(why);

        if (voltageKv is { } kv)
        {
            var points = kv >= 230 ? 8 : kv >= 115 ? 5 : kv >= 60 ? 2 : 0;
            score += points;
            if (points >= 5) reasons.Add($"{kv} kV line");
        }

        var waited = now - f.CreatedAt;
        if (waited > TimeSpan.Zero)
        {
            score += Math.Min(MaxWaitPoints, waited.TotalHours);
            if (waited >= TimeSpan.FromHours(1)) reasons.Add($"waiting {Describe(waited)}");
        }

        var repairBy = RepairWindow(f.Severity) is { } window ? f.CreatedAt + window : (DateTimeOffset?)null;
        if (repairBy is { } due && now > due)
        {
            score += 40;
            reasons.Add("past repair-by");
        }
        if (f.Escalated)
        {
            score += 5;
            reasons.Add("escalated");
        }
        return new(Math.Round(score, 1), reasons, repairBy);
    }

    /// <summary>
    /// The forward schedule: every triaged work order not yet sent, most important first, each planned onto the
    /// qualified, on-duty technician who'd be back from it soonest, taking into account when they're back from the jobs
    /// they're on, the hours left in their shift (and the rest before the next one), and when a truck is free at their
    /// yard. Each job planned ties that person and truck up for it, so the next one down plans around it.
    /// Estimates go by straight-line distance and one person per job (a ride-along makes repairs quicker), so the plan
    /// is redone whenever it's looked at. <paramref name="trucks"/>: the trucks that can be sent (null: trucks aren't
    /// tracked).
    /// </summary>
    public static IReadOnlyList<ScheduledJob> Plan(IEnumerable<Finding> findings, IReadOnlyList<Crew> crews, IReadOnlyList<Depot> depots,
        IReadOnlyList<Vehicle>? trucks, Func<string, int?> voltageOf, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(crews);
        ArgumentNullException.ThrowIfNull(depots);
        ArgumentNullException.ThrowIfNull(voltageOf);
        var all = findings.ToList();
        var history = FieldWork.History(all);
        var hours = new FieldHours(history, now);
        var active = history.Where(d => d.IsActive(now)).ToList();

        // Everyone who could go out: when they're next free, and where their shift stands then.
        var people = new List<Slot>();
        foreach (var c in crews.Where(c => c.Status == CrewStatus.Available))
            foreach (var m in c.Members)
            {
                if (depots.FirstOrDefault(d => d.Id == c.DepotOf(m)) is not { } depot) continue;
                var shift = hours.Of(c, m);
                var backAt = FieldWork.BusyUntil(active, c.Id, m.Name);
                var free = Later(now, backAt ?? now);
                var lastEnd = history.Where(d => d.Involves(c.Id, m.Name)).Max(d => (DateTimeOffset?)d.ReturnsAt);
                var used = shift.Booked;
                if (shift.RestingUntil is { } rested) (free, used) = (Later(free, rested), TimeSpan.Zero);
                people.Add(new Slot(c, m, depot, free, used, lastEnd));
            }
        var truckFree = trucks?.Where(v => v.Status == VehicleStatus.InService)
            .ToDictionary(v => v.Id, v => Later(now, active.Where(d => d.VehicleId == v.Id).Max(d => (DateTimeOffset?)d.ReturnsAt) ?? now));

        var pending = all.Where(f => f.Status == FindingStatus.Triaged)
            .Select(f => (Finding: f, Priority: Priority(f, voltageOf(f.CircuitId), now)))
            .OrderByDescending(x => x.Finding.Severity).ThenByDescending(x => x.Priority.Score).ThenBy(x => x.Finding.CreatedAt)
            .ThenBy(x => x.Finding.Id, StringComparer.Ordinal)
            .ToList();

        var plan = new List<ScheduledJob>(pending.Count);
        foreach (var (f, p) in pending)
        {
            ScheduledJob Job(Slot? s, Vehicle? v, DateTimeOffset? start, DateTimeOffset? repaired, DateTimeOffset? back, string? blocked) =>
                new(plan.Count + 1, f.Id, f.StructureLabel, f.CircuitId, f.Type, f.Severity, p.Score, p.Reasons, f.CreatedAt, p.RepairBy,
                    s is null ? null : new CrewAssignee(s.Crew, s.Member).Label, s?.Crew.Id, s?.Member.Name, s?.Depot.Name, v?.Id,
                    start, repaired, back, start is { } st && st <= now, repaired is { } r && p.RepairBy is { } due && r > due, blocked);

            var qualified = people.Where(s => s.Crew.CanFix(f.Type, s.Member)).ToList();
            if (qualified.Count == 0)
            {
                plan.Add(Job(null, null, null, null, null, $"Nobody on duty is qualified to fix {FindingImage.Label(f.Type).ToLowerInvariant()}."));
                continue;
            }

            (Slot Slot, Vehicle? Truck, DateTimeOffset Start, TimeSpan Length)? best = null;
            foreach (var s in qualified)
            {
                var length = Shifts.Estimate(s.Depot.Location, f.Location, s.Depot.Location, f.Severity);
                if (length > Shifts.Limit) continue; // can't be done in one shift from this yard
                var start = s.Free;
                // Not enough of the shift left (or too little to bother going out again): rest first.
                if (s.UsedAt(start) is var used && used > TimeSpan.Zero && (used + length > Shifts.Limit || Shifts.Limit - used < Shifts.ClockOutUnder))
                    start = Later(start, (s.LastEnd ?? start) + Shifts.Rest);
                Vehicle? truck = null;
                if (truckFree is not null)
                {
                    var atYard = trucks!.Where(v => v.DepotId == s.Depot.Id && truckFree.ContainsKey(v.Id))
                        .OrderBy(v => truckFree[v.Id]).ThenBy(v => v.Seats).ThenBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (atYard is null) continue; // no truck in service at their yard
                    truck = atYard;
                    start = Later(start, truckFree[atYard.Id]);
                }
                if (best is null || start + length < best.Value.Start + best.Value.Length
                    || (start + length == best.Value.Start + best.Value.Length && Geo.DistanceMeters(s.Depot.Location, f.Location) < Geo.DistanceMeters(best.Value.Slot.Depot.Location, f.Location)))
                    best = (s, truck, start, length);
            }
            if (best is not { } pick)
            {
                plan.Add(Job(null, null, null, null, null, truckFree is null
                    ? "Too far for anyone qualified to reach within a shift."
                    : "No truck in service at the yards of the people qualified to fix it."));
                continue;
            }

            var slot = pick.Slot;
            var begin = pick.Start;
            var end = begin + pick.Length;
            var repairedAt = end - FieldWork.TravelTime(Geo.DistanceMeters(f.Location, slot.Depot.Location) / 1000 * Shifts.RoadFactor);
            slot.Used = slot.UsedAt(begin) + pick.Length;
            slot.Free = end;
            slot.LastEnd = end;
            if (pick.Truck is { } t) truckFree![t.Id] = end;
            plan.Add(Job(slot, pick.Truck, begin, repairedAt, end, null));
        }
        return plan;
    }

    private sealed class Slot(Crew crew, CrewMember member, Depot depot, DateTimeOffset free, TimeSpan used, DateTimeOffset? lastEnd)
    {
        public Crew Crew { get; } = crew;
        public CrewMember Member { get; } = member;
        public Depot Depot { get; } = depot;
        public DateTimeOffset Free { get; set; } = free;
        public TimeSpan Used { get; set; } = used;
        public DateTimeOffset? LastEnd { get; set; } = lastEnd;

        /// <summary>Shift time used if they set off at <paramref name="at"/>: none after a rest-length break.</summary>
        public TimeSpan UsedAt(DateTimeOffset at) => LastEnd is { } last && at - last >= Shifts.Rest ? TimeSpan.Zero : Used;
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{(int)t.TotalDays} days" : t.TotalHours >= 2 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalMinutes} min";
}
