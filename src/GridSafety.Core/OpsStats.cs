namespace GridSafety.Core;

public sealed record OpsStats(
    int ActiveMissions,
    int DronesFlying,
    int MissionsLast7Days,
    double MilesFlownLast7Days,
    int OpenFindings,
    IReadOnlyDictionary<int, int> OpenBySeverity,
    int OverdueTriage,
    double? MedianHoursToTriage)
{
    public static OpsStats Compute(IReadOnlyCollection<Mission> missions, IReadOnlyCollection<Finding> findings, IReadOnlyCollection<DroneState> drones, DateTimeOffset now)
    {
        var open = findings.Where(f => !Finding.Workflow.IsTerminal(f.Status)).ToList();
        var recent = missions.Where(m => m.CreatedAt >= now.AddDays(-7)).ToList();

        var triageHours = findings
            .Select(f => f.History.FirstOrDefault(h => h.From == FindingStatus.New) is { } h ? (h.At - f.CreatedAt).TotalHours : (double?)null)
            .OfType<double>()
            .Order()
            .ToList();

        return new OpsStats(
            ActiveMissions: missions.Count(m => m.Status == MissionStatus.InFlight),
            DronesFlying: drones.Count(d => d.Status == DroneStatus.Flying),
            MissionsLast7Days: recent.Count,
            MilesFlownLast7Days: Math.Round(recent.Sum(m => m.DistanceFlownMeters) / 1609.344, 1),
            OpenFindings: open.Count,
            OpenBySeverity: Enumerable.Range(1, 5).ToDictionary(s => s, s => open.Count(f => f.Severity == s)),
            OverdueTriage: open.Count(f => f.IsOverdue(now)),
            MedianHoursToTriage: triageHours.Count == 0 ? null : Math.Round(Median(triageHours), 1));
    }

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}
