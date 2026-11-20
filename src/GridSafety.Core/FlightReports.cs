namespace GridSafety.Core;

/// <summary>One mission in a drone's or pilot's flight log.</summary>
public sealed record FlightLogEntry(
    string MissionId, string? Name, string CircuitId, string CircuitName, string DroneId, string Pilot, MissionStatus Status,
    DateTimeOffset PlannedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, double FlightMinutes, double DistanceMeters, double Coverage,
    int StructuresInspected, int Findings, int Alerts, int CriticalAlerts, double? EndBatteryPct);

/// <summary>Totals across a flight log. Alerts are counted from each mission's recent alerts.</summary>
public sealed record FlightTotals(
    int Missions, int Completed, int Aborted, double FlightHours, double DistanceMeters, int StructuresInspected, int Findings,
    IReadOnlyDictionary<AlertKind, int> AlertsByKind, DateTimeOffset? FirstFlight, DateTimeOffset? LastFlight)
{
    /// <summary>Flight hours at the flight crew rate (pilot plus observer, see <see cref="Costs"/>).</summary>
    public double CrewCost => Costs.Money(FlightHours * Costs.FlightCrewPerHour);
}

public sealed record FlightReport(FlightTotals Totals, IReadOnlyList<FlightLogEntry> Flights, IReadOnlyList<FlightDay> Days);

/// <summary>One Pacific calendar day of a flight log, oldest first; a mission counts on the day it took off (or was planned).</summary>
public sealed record FlightDay(DateOnly Day, int Missions, int Completed, int Aborted, double FlightHours, double DistanceMeters,
    int StructuresInspected, int Findings, int Alerts)
{
    public double CrewCost => Costs.Money(FlightHours * Costs.FlightCrewPerHour);
}

/// <summary>Flight logs for drones and pilots, from their missions (newest first).</summary>
public static class FlightReports
{
    /// <param name="missions">The missions to report on, already picked for the drone or pilot.</param>
    /// <param name="findCircuit">Looks up a circuit for its name and coverage; null when it's gone.</param>
    public static FlightReport Of(IEnumerable<Mission> missions, Func<string, Circuit?> findCircuit, int max = 100)
    {
        var all = missions.OrderByDescending(m => m.StartedAt ?? m.CreatedAt).ToList();
        var flights = all.Take(max).Select(m =>
        {
            var circuit = findCircuit(m.CircuitId);
            var minutes = m.StartedAt is { } s ? Math.Max(0, ((m.EndedAt ?? m.UpdatedAt) - s).TotalMinutes) : 0;
            return new FlightLogEntry(m.Id, m.Name, m.CircuitId, circuit?.Name ?? m.CircuitId, m.DroneId, m.Pilot, m.Status,
                m.CreatedAt, m.StartedAt, m.EndedAt, Math.Round(minutes, 1), Math.Round(m.DistanceFlownMeters), circuit is null ? 0 : m.Coverage(circuit),
                m.StructuresInspected.Count, m.FindingCount, m.RecentAlerts.Count, m.RecentAlerts.Count(a => a.Level == AlertLevel.Critical),
                Mission.Workflow.IsTerminal(m.Status) ? m.BatteryPct : null);
        }).ToList();

        var flown = all.Where(m => m.StartedAt is not null).ToList();
        var days = all.GroupBy(m => Periods.DayOf(m.StartedAt ?? m.CreatedAt)).OrderBy(g => g.Key)
            .Select(g => new FlightDay(g.Key, g.Count(), g.Count(m => m.Status == MissionStatus.Completed), g.Count(m => m.Status == MissionStatus.Aborted),
                Math.Round(g.Sum(Hours), 2), Math.Round(g.Sum(m => m.DistanceFlownMeters)), g.Sum(m => m.StructuresInspected.Count),
                g.Sum(m => m.FindingCount), g.Sum(m => m.RecentAlerts.Count)))
            .ToList();
        var totals = new FlightTotals(
            all.Count, all.Count(m => m.Status == MissionStatus.Completed), all.Count(m => m.Status == MissionStatus.Aborted),
            Math.Round(flown.Sum(Hours), 2),
            Math.Round(all.Sum(m => m.DistanceFlownMeters)), all.Sum(m => m.StructuresInspected.Count), all.Sum(m => m.FindingCount),
            all.SelectMany(m => m.RecentAlerts).CountBy(a => a.Kind).ToDictionary(),
            flown.Min(m => m.StartedAt), flown.Max(m => m.StartedAt));
        return new FlightReport(totals, flights, days);
    }

    private static double Hours(Mission m) => m.StartedAt is { } s ? Math.Max(0, ((m.EndedAt ?? m.UpdatedAt) - s).TotalHours) : 0;
}
