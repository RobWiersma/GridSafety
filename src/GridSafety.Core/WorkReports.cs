namespace GridSafety.Core;

/// <summary>
/// Totals for a work report. Hours are simulated: <see cref="DriveHours"/> and <see cref="RepairHours"/> are truck time,
/// <see cref="PersonHours"/> counts each person in the truck (two people for an hour is two person-hours).
/// <see cref="Trips"/> counts truck rolls; a trip that went on from one job to the next counts once.
/// </summary>
public sealed record WorkTotals(int Trips, int Jobs, int Repaired, int CalledBack, double DriveHours, double RepairHours, double PersonHours)
{
    /// <summary>Each person's hours at their own rate (see <see cref="PayRates"/>).</summary>
    public double LaborCost { get; init; }
}

/// <summary>One line of a report's breakdown: a person, or a truck, with the jobs they were on and their time on them.</summary>
public sealed record WorkBreakdown(string Name, string? Detail, int Jobs, int Repaired, double Hours);

/// <summary>
/// The field work done by a truck, a crew or out of a yard: totals, a breakdown (per person or per truck), and every
/// job, newest first. In each job, <see cref="ShiftJob.With"/> is everyone who was in the truck.
/// </summary>
public sealed record WorkReport(WorkTotals Totals, IReadOnlyList<WorkBreakdown> Breakdown, IReadOnlyList<ShiftJob> Jobs, IReadOnlyList<WorkDay> Days);

/// <summary>
/// One Pacific calendar day of a work report, oldest first. A job counts on the day its truck left, hours and all, so the
/// days add up to the report's totals.
/// </summary>
public sealed record WorkDay(DateOnly Day, int Trips, int Jobs, int Repaired, int CalledBack, double DriveHours, double RepairHours, double PersonHours)
{
    public double LaborCost { get; init; }
}

/// <summary>Builds work reports from the truck rolls on record.</summary>
public static class WorkReports
{
    /// <summary>
    /// The report for the jobs <paramref name="include"/> picks, broken down by the names <paramref name="keysOf"/> gives
    /// each job. <paramref name="always"/> lists breakdown lines to show even with no jobs (e.g. every truck at a yard),
    /// with a detail line each.
    /// </summary>
    public static WorkReport Of(IEnumerable<Finding> findings, Func<FieldDispatch, bool> include, Func<FieldDispatch, IEnumerable<string>> keysOf,
        DateTimeOffset now, IReadOnlyList<(string Name, string? Detail)>? always = null, int max = 200, PayRates? rates = null)
    {
        rates ??= PayRates.Standard;
        // Labor: the job's simulated hours for everyone in the truck, each at their own rate.
        double Labor(IEnumerable<Finding> fs) => Costs.Money(fs.Sum(f => Shifts.DemoSpeed * Spent(f.Dispatch!, now).TotalHours * rates.Truck(f.Dispatch!)));
        var all = findings.Where(f => f.Dispatch is not null).ToList();
        var picked = all.Where(f => include(f.Dispatch!)).OrderByDescending(f => f.Dispatch!.DepartedAt).ToList();
        // A job that another one handed over to isn't a new trip.
        var handedOver = all.Select(f => f.Dispatch!.NextFindingId).OfType<string>().ToHashSet();

        var jobs = picked.Select(f => Shifts.Job(f, f.Dispatch!, now, member: "")).ToList();
        var lines = (always ?? []).ToDictionary(a => a.Name, a => (a.Detail, Jobs: 0, Repaired: 0, Time: TimeSpan.Zero), StringComparer.OrdinalIgnoreCase);
        var personTime = TimeSpan.Zero;
        foreach (var (f, j) in picked.Zip(jobs))
        {
            var d = f.Dispatch!;
            var spent = Spent(d, now);
            personTime += spent * People(d);
            foreach (var key in keysOf(d).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var line = lines.GetValueOrDefault(key);
                lines[key] = (line.Detail, line.Jobs + 1, line.Repaired + (j.Outcome == JobOutcome.Repaired ? 1 : 0), line.Time + spent);
            }
        }

        var totals = new WorkTotals(
            picked.Count(f => !handedOver.Contains(f.Id)), jobs.Count,
            jobs.Count(j => j.Outcome == JobOutcome.Repaired), jobs.Count(j => j.Outcome == JobOutcome.CalledBack),
            Math.Round(jobs.Sum(j => j.DriveHours), 1), Math.Round(jobs.Sum(j => j.RepairHours), 1), Shifts.Hours(personTime)) { LaborCost = Labor(picked) };
        var breakdown = lines.Select(kv => new WorkBreakdown(kv.Key, kv.Value.Detail, kv.Value.Jobs, kv.Value.Repaired, Shifts.Hours(kv.Value.Time)))
            .OrderByDescending(b => b.Hours).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
        var days = picked.Zip(jobs).GroupBy(x => Periods.DayOf(x.First.Dispatch!.DepartedAt)).OrderBy(g => g.Key)
            .Select(g => new WorkDay(g.Key,
                g.Count(x => !handedOver.Contains(x.First.Id)), g.Count(),
                g.Count(x => x.Second.Outcome == JobOutcome.Repaired), g.Count(x => x.Second.Outcome == JobOutcome.CalledBack),
                Math.Round(g.Sum(x => x.Second.DriveHours), 1), Math.Round(g.Sum(x => x.Second.RepairHours), 1),
                Shifts.Hours(g.Aggregate(TimeSpan.Zero, (t, x) => t + Spent(x.First.Dispatch!, now) * People(x.First.Dispatch!))))
            { LaborCost = Labor(g.Select(x => x.First)) })
            .ToList();
        return new WorkReport(totals, breakdown, [.. jobs.Take(max)], days);
    }

    /// <summary>Who was in the truck: the riders, or the technician (or crew) sent.</summary>
    public static IEnumerable<string> PeopleOf(FieldDispatch d, Crew? wholeCrew = null) =>
        d.Riders is { Count: > 0 } r ? r.Select(x => x.Name)
        : d.Member is { } m ? [m]
        : wholeCrew?.Members.Select(x => x.Name) ?? [d.Assignee];

    private static int People(FieldDispatch d) => d.Riders?.Count ?? 1;

    /// <summary>Time the job has taken so far, from leaving to getting back (or handing over to the next job).</summary>
    private static TimeSpan Spent(FieldDispatch d, DateTimeOffset now) =>
        now <= d.DepartedAt ? TimeSpan.Zero : (now < d.ReturnsAt ? now : d.ReturnsAt) - d.DepartedAt;
}
