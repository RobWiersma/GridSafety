using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

public class ResourceReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
    private static readonly GeoPoint Richmond = CrewRoster.Depots.Single(d => d.Id == "richmond").Location;

    private static Finding Defect(string id, double kmEast) => new()
    {
        Id = id, MissionId = "m1", CircuitId = "c1", StructureId = id, StructureLabel = id.ToUpperInvariant(), Type = FindingType.ConductorDamage,
        Severity = 3, Location = new GeoPoint(Richmond.Lat, Richmond.Lon + kmEast / 88.0), CreatedAt = T0, Status = FindingStatus.Triaged,
    };

    private static async Task<FieldDispatch> Send(Finding f, DateTimeOffset now, Finding? previous = null)
    {
        IReadOnlyList<FieldDispatch> active = previous?.Dispatch is { } p ? [p] : [];
        var lenny = CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, f.Type, f.Location, active).First(c => c.Member?.Name == "Lenny Leonardo");
        f.MoveTo(FindingStatus.Assigned, now, "dana", assignee: lenny.Label);
        return f.Dispatch = (await FieldWork.PlanAsync(lenny, f, previous, new FakeRouteSource(), now))!;
    }

    [Fact]
    public async Task A_technicians_report_splits_their_work_into_shifts_with_each_job()
    {
        // Shift 1: two jobs back to back. Shift 2: one job after a full rest.
        var a = Defect("sa-1", 8);
        var b = Defect("sa-2", 11);
        await Send(a, T0);
        await Send(b, T0 + TimeSpan.FromSeconds(5), previous: a);
        var c = Defect("sa-3", 6);
        var second = b.Dispatch!.ReturnsAt + Shifts.Rest + TimeSpan.FromSeconds(30);
        await Send(c, second);

        var now = c.Dispatch!.ReturnsAt + TimeSpan.FromSeconds(1);
        var reports = Shifts.Reports([a, b, c], "sector-7g", "Lenny Leonardo", now);

        Assert.Equal(2, reports.Count);
        var (latest, earlier) = (reports[0], reports[1]);
        Assert.Equal(["SA-3"], latest.Jobs.Select(j => j.StructureLabel));
        Assert.Equal(["SA-1", "SA-2"], earlier.Jobs.Select(j => j.StructureLabel));
        Assert.Equal(ShiftStatus.Done, earlier.Status);
        Assert.Equal(ShiftStatus.OnCall, latest.Status);
        Assert.NotNull(earlier.RestHours);
        Assert.True(earlier.RestHours >= Shifts.RestHours);

        // Drive and repair add up to the time worked; the first job went straight on to the second.
        Assert.Equal(earlier.WorkedHours, earlier.DriveHours + earlier.RepairHours, 0.2);
        Assert.Equal("SA-2", earlier.Jobs[0].NextSiteLabel);
        Assert.All(earlier.Jobs, j => Assert.Equal(JobOutcome.Repaired, j.Outcome));
        Assert.Equal(Shifts.Hours(FieldWork.RepairTime(3)), earlier.Jobs[0].RepairHours);
    }

    [Fact]
    public async Task Jobs_not_reached_yet_are_booked_not_worked()
    {
        var a = Defect("sa-1", 8);
        var d = await Send(a, T0);
        var midway = T0 + (d.ArrivesAt - T0) / 2;
        var r = Shifts.Reports([a], "sector-7g", "Lenny Leonardo", midway).Single();
        Assert.Equal(ShiftStatus.Working, r.Status);
        Assert.True(r.WorkedHours < r.BookedHours);
        Assert.Equal(JobOutcome.InProgress, r.Jobs.Single().Outcome);
    }

    [Fact]
    public void A_flight_log_lists_missions_newest_first_with_totals()
    {
        var circuit = TestCircuits.Line;
        Mission Flight(int minutesAgo, MissionStatus end, double meters, int findings)
        {
            var m = Mission.Plan(circuit, "DRN-1", "Pat", null, T0.AddMinutes(-minutesAgo - 5));
            m.MoveTo(MissionStatus.InFlight, T0.AddMinutes(-minutesAgo));
            m.StartedAt = T0.AddMinutes(-minutesAgo);
            m.MoveTo(end, T0.AddMinutes(-minutesAgo + 12));
            m.EndedAt = T0.AddMinutes(-minutesAgo + 12);
            m.DistanceFlownMeters = meters;
            m.FindingCount = findings;
            m.BatteryPct = 31;
            return m;
        }
        var old = Flight(120, MissionStatus.Completed, 4000, 2);
        var recent = Flight(30, MissionStatus.Aborted, 1500, 1);
        var planned = Mission.Plan(circuit, "DRN-1", "Pat", null, T0);

        var r = FlightReports.Of([old, recent, planned], id => id == circuit.Id ? circuit : null);

        Assert.Equal([planned.Id, recent.Id, old.Id], r.Flights.Select(f => f.MissionId));
        Assert.Equal(3, r.Totals.Missions);
        Assert.Equal(1, r.Totals.Completed);
        Assert.Equal(1, r.Totals.Aborted);
        Assert.Equal(0.4, r.Totals.FlightHours, 2); // two 12-minute flights
        Assert.Equal(5500, r.Totals.DistanceMeters);
        Assert.Equal(3, r.Totals.Findings);
        Assert.Equal(12, r.Flights[1].FlightMinutes);
        Assert.Equal(31, r.Flights[1].EndBatteryPct);
        Assert.Null(r.Flights[0].StartedAt);
        Assert.Equal(circuit.Name, r.Flights[0].CircuitName);
    }
}
