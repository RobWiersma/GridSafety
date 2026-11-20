namespace GridSafety.Core;

/// <summary>
/// One Pacific calendar day of the whole operation: what the drones flew, what they found, what triage did with it, and
/// the field work that followed. Flight hours are real time; field hours are simulated (see <see cref="Shifts.DemoSpeed"/>).
/// </summary>
public sealed record ActivityDay(
    DateOnly Day,
    int Missions, int Completed, int Aborted, double FlightHours, double DistanceMeters, int StructuresInspected,
    int Found, int FoundSevere, int Triaged, int Dismissed, int Escalated,
    int Trips, int Jobs, int Repaired, int CalledBack, double DriveHours, double RepairHours, double PersonHours,
    int Refuels = 0, double Gallons = 0, double FuelCost = 0, int BatterySwaps = 0, int PacksReplaced = 0, int ShopVisits = 0, double MaintenanceCost = 0)
{
    /// <summary>Technicians' person-hours at the labor rate.</summary>
    public double LaborCost => Costs.Money(PersonHours * Costs.LaborPerPersonHour);
    /// <summary>Flight crews' time in the air.</summary>
    public double FlightCost => Costs.Money(FlightHours * Costs.FlightCrewPerHour);
    /// <summary>Labor, flight crews, fuel bought, and service, inspections, batteries and repairs.</summary>
    public double TotalCost => Costs.Money(LaborCost + FlightCost + FuelCost + MaintenanceCost);
}

/// <summary>Activity across everything, day by day.</summary>
public static class ActivityReports
{
    /// <summary>
    /// Every day with any activity, oldest first. A finding counts as found on the day it was created, and as triaged
    /// or dismissed on the day that happened; a mission on the day it took off; a field job on the day its truck left.
    /// </summary>
    /// <param name="upkeep">Every truck's and drone's maintenance events; each counts on the day it started.</param>
    public static IReadOnlyList<ActivityDay> Daily(IReadOnlyList<Finding> findings, IEnumerable<Mission> missions, DateTimeOffset now,
        IEnumerable<MaintenanceEvent>? upkeep = null)
    {
        var shop = (upkeep ?? []).GroupBy(e => Periods.DayOf(e.At)).ToDictionary(g => g.Key, g => g.ToList());
        var flights = FlightReports.Of(missions, _ => null, max: 0).Days.ToDictionary(d => d.Day);
        var work = WorkReports.Of(findings, _ => true, _ => [], now, max: 0).Days.ToDictionary(d => d.Day);

        var found = findings.CountBy(f => Periods.DayOf(f.CreatedAt)).ToDictionary();
        var severe = findings.Where(f => f.Severity >= 4).CountBy(f => Periods.DayOf(f.CreatedAt)).ToDictionary();
        var escalated = findings.Where(f => f.Escalated && f.TriageDueAt is not null).CountBy(f => Periods.DayOf(f.TriageDueAt!.Value)).ToDictionary();
        // Triaged: left New for a work order (Triaged, or straight to Assigned); dismissed: closed without one.
        var moves = findings.SelectMany(f => f.History).Where(h => h.From == FindingStatus.New).ToList();
        var triaged = moves.Where(h => h.To is FindingStatus.Triaged or FindingStatus.Assigned).CountBy(h => Periods.DayOf(h.At)).ToDictionary();
        var dismissed = moves.Where(h => h.To == FindingStatus.Dismissed).CountBy(h => Periods.DayOf(h.At)).ToDictionary();

        return flights.Keys.Concat(work.Keys).Concat(found.Keys).Concat(triaged.Keys).Concat(dismissed.Keys).Concat(shop.Keys).Distinct().Order()
            .Select(day =>
            {
                var f = flights.GetValueOrDefault(day);
                var w = work.GetValueOrDefault(day);
                var u = shop.GetValueOrDefault(day) ?? [];
                var fuel = u.Where(e => e.Kind == MaintenanceKind.Refuel).ToList();
                return new ActivityDay(day,
                    f?.Missions ?? 0, f?.Completed ?? 0, f?.Aborted ?? 0, f?.FlightHours ?? 0, f?.DistanceMeters ?? 0, f?.StructuresInspected ?? 0,
                    found.GetValueOrDefault(day), severe.GetValueOrDefault(day), triaged.GetValueOrDefault(day), dismissed.GetValueOrDefault(day),
                    escalated.GetValueOrDefault(day),
                    w?.Trips ?? 0, w?.Jobs ?? 0, w?.Repaired ?? 0, w?.CalledBack ?? 0, w?.DriveHours ?? 0, w?.RepairHours ?? 0, w?.PersonHours ?? 0,
                    fuel.Count, Math.Round(fuel.Sum(e => e.Gallons ?? 0), 1), Math.Round(fuel.Sum(e => e.Cost ?? 0), 2),
                    u.Count(e => e.Kind == MaintenanceKind.BatterySwap), u.Count(e => e.Kind == MaintenanceKind.BatteryReplaced),
                    u.Count(e => e.Kind is MaintenanceKind.Service or MaintenanceKind.Inspection or MaintenanceKind.Repair),
                    Costs.Money(u.Where(e => e.Kind != MaintenanceKind.Refuel).Sum(e => e.Cost ?? 0)));
            })
            .ToList();
    }
}
