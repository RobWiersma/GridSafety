using GridSafety.Core;
using System.Globalization;
using Xunit;

namespace GridSafety.Tests;

public class ScheduleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-18T18:00:00Z", CultureInfo.InvariantCulture);
    private static readonly Depot Yard = new("yard", "Test yard", new(37.90, -122.00));
    private static readonly Depot FarYard = new("far", "Far yard", new(38.30, -121.60));

    private static Crew Crew(params CrewMember[] members) =>
        new("line", "Line Crew", "Line work", [FindingType.CrossarmDamage, FindingType.ConductorDamage, FindingType.VegetationEncroachment], members, DepotId: "yard");

    private static Finding Open(string id, FindingType type, int severity, DateTimeOffset? found = null, double lat = 37.91) => new()
    {
        Id = id, MissionId = "m", CircuitId = "c", StructureId = id, StructureLabel = id, Type = type, Severity = severity,
        Location = new(lat, -122.00), CreatedAt = found ?? Now, UpdatedAt = found ?? Now, Status = FindingStatus.Triaged,
    };

    private static IReadOnlyList<ScheduledJob> Plan(IEnumerable<Finding> findings, Crew crew, IReadOnlyList<Vehicle>? trucks = null) =>
        WorkSchedule.Plan(findings, [crew], [Yard, FarYard], trucks, _ => 115, Now);

    [Fact]
    public void Work_is_ranked_by_severity_then_risk_then_how_long_it_has_waited()
    {
        var critical = Open("critical", FindingType.CrossarmDamage, 5);
        var moderateVegetation = Open("veg", FindingType.VegetationEncroachment, 3);
        var moderateHardware = Open("hardware", FindingType.MissingHardware, 3);
        var oldHardware = Open("old-hardware", FindingType.MissingHardware, 3, Now.AddDays(-2));
        var lateHigh = Open("late", FindingType.MissingHardware, 4, Now.AddDays(-8)); // past its week

        Assert.Contains("fire risk", WorkSchedule.Priority(moderateVegetation, 115, Now).Reasons);
        Assert.Contains("past repair-by", WorkSchedule.Priority(lateHigh, null, Now).Reasons);
        Assert.Equal(Now.AddDays(1), WorkSchedule.Priority(critical, null, Now).RepairBy);
        Assert.Null(WorkSchedule.Priority(Open("low", FindingType.MissingHardware, 2), null, Now).RepairBy);

        var crew = Crew(new CrewMember("Lenny", "Lineman", [FindingType.CrossarmDamage, FindingType.MissingHardware, FindingType.VegetationEncroachment]));
        var plan = Plan([moderateHardware, oldHardware, moderateVegetation, lateHigh, critical], crew);
        Assert.Equal(["critical", "late", "old-hardware", "veg", "hardware"], plan.Select(j => j.FindingId));
        Assert.Equal([1, 2, 3, 4, 5], plan.Select(j => j.Rank));
        // A moderate job that has waited all day still never outranks a critical one.
        Assert.True(plan[0].Score > plan[2].Score);
    }

    [Fact]
    public void Jobs_nobody_can_start_now_are_planned_for_when_someone_is_back()
    {
        var crew = Crew(new CrewMember("Lenny", "Lineman"));
        var plan = Plan([Open("a", FindingType.CrossarmDamage, 4), Open("b", FindingType.CrossarmDamage, 3)], crew);

        Assert.True(plan[0].StartsNow);
        Assert.Equal(Now, plan[0].StartsAt);
        Assert.Equal("Lenny (Line Crew)", plan[0].Assignee);
        Assert.True(plan[0].RepairedAt < plan[0].BackAt);
        // The second waits for Lenny to get back from the first.
        Assert.False(plan[1].StartsNow);
        Assert.Equal(plan[0].BackAt, plan[1].StartsAt);
        Assert.False(plan[1].Late);
    }

    [Fact]
    public void Two_people_share_the_work_and_a_yard_with_one_truck_sends_one_at_a_time()
    {
        var crew = Crew(new CrewMember("Lenny", "Lineman"), new CrewMember("Carl", "Lineman"));
        var jobs = new[] { Open("a", FindingType.CrossarmDamage, 4), Open("b", FindingType.CrossarmDamage, 4, Now.AddMinutes(-1)) };

        var noTrucks = Plan(jobs, crew);
        Assert.All(noTrucks, j => Assert.True(j.StartsNow));
        Assert.Equal(2, noTrucks.Select(j => j.Member).Distinct().Count());

        var oneTruck = Plan(jobs, crew, [new Vehicle("T-1", VehicleKind.Pickup, 2, "yard")]);
        Assert.True(oneTruck[0].StartsNow);
        Assert.Equal("T-1", oneTruck[0].VehicleId);
        Assert.Equal(oneTruck[0].BackAt, oneTruck[1].StartsAt);

        // No truck in service at the yard: nobody can go.
        var none = Plan(jobs, crew, [new Vehicle("T-9", VehicleKind.Pickup, 2, "far")]);
        Assert.All(none, j => Assert.NotNull(j.Blocked));
        Assert.All(none, j => Assert.Null(j.Assignee));
    }

    [Fact]
    public void A_full_shift_means_resting_before_the_next_job_and_unqualified_work_is_flagged()
    {
        var crew = Crew(new CrewMember("Lenny", "Lineman", [FindingType.CrossarmDamage]));
        // Far enough that each round trip is a big chunk of an 8 h shift.
        var far = Enumerable.Range(0, 6).Select(i => Open($"far-{i}", FindingType.CrossarmDamage, 3, Now.AddMinutes(-i), lat: 38.25)).ToList();
        var plan = Plan([.. far, Open("thermal", FindingType.ThermalHotspot, 5)], crew);

        var thermal = plan.Single(j => j.FindingId == "thermal");
        Assert.Equal(1, thermal.Rank); // still ranked first
        Assert.Null(thermal.Assignee);
        Assert.Contains("qualified", thermal.Blocked);

        var lenny = plan.Where(j => j.Assignee is not null).ToList();
        Assert.Equal(6, lenny.Count);
        // Somewhere along the line he runs out of shift and rests a full 10 h (simulated) before going again.
        var gaps = lenny.Zip(lenny.Skip(1), (a, b) => b.StartsAt!.Value - a.BackAt!.Value).ToList();
        Assert.Contains(gaps, g => g >= Shifts.Rest);
        Assert.All(gaps, g => Assert.True(g == TimeSpan.Zero || g >= Shifts.Rest));
    }
}
