using GridSafety.Core;
using System.Globalization;
using Xunit;

namespace GridSafety.Tests;

public class PeriodTests
{
    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-03-08T09:59:00Z", -8)] // 1:59 am PST, just before the clocks go forward
    [InlineData("2026-03-08T10:00:00Z", -7)]
    [InlineData("2026-07-01T00:00:00Z", -7)]
    [InlineData("2026-11-01T08:59:00Z", -7)] // 1:59 am PDT, the first time round
    [InlineData("2026-11-01T09:00:00Z", -8)]
    [InlineData("2026-12-25T12:00:00Z", -8)]
    public void Pacific_offset_follows_us_daylight_time(string utc, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), Periods.OffsetAt(At(utc)));

    [Fact]
    public void An_evening_in_california_is_still_the_same_day_though_utc_has_moved_on()
    {
        Assert.Equal(new DateOnly(2026, 11, 19), Periods.DayOf(At("2026-11-20T07:30:00Z"))); // 11:30 pm PST
        Assert.Equal(new DateOnly(2026, 11, 20), Periods.DayOf(At("2026-11-20T08:00:00Z")));
    }

    [Fact]
    public void Activity_counts_each_thing_on_the_day_it_happened()
    {
        var mon = At("2026-11-16T18:00:00Z"); // 10 am Monday in California
        var tue = mon.AddDays(1);
        Finding Found(string id, int severity) => new()
        {
            Id = id, MissionId = "m1", CircuitId = "c1", StructureId = id, StructureLabel = id, Type = FindingType.ConductorDamage,
            Severity = severity, CreatedAt = mon, UpdatedAt = mon,
        };
        var kept = Found("f1", 4);
        var dropped = Found("f2", 2);
        var waiting = Found("f3", 1);
        kept.MoveTo(FindingStatus.Triaged, tue, "dana");
        dropped.MoveTo(FindingStatus.Dismissed, tue, "dana");
        var flight = new Mission
        {
            Id = "m1", CircuitId = "c1", DroneId = "D-1", Pilot = "Edna", Status = MissionStatus.Completed, CreatedAt = mon,
            StartedAt = mon, EndedAt = mon + TimeSpan.FromHours(1), UpdatedAt = mon + TimeSpan.FromHours(1),
        };

        var days = ActivityReports.Daily([kept, dropped, waiting], [flight], tue);

        Assert.Equal([new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17)], days.Select(d => d.Day));
        Assert.Equal((1, 1.0, 3, 1, 0, 0), (days[0].Missions, days[0].FlightHours, days[0].Found, days[0].FoundSevere, days[0].Triaged, days[0].Dismissed));
        Assert.Equal((0, 0, 1, 1), (days[1].Missions, days[1].Found, days[1].Triaged, days[1].Dismissed));
        Assert.Equal(Costs.FlightCrewPerHour, days[0].FlightCost); // one flight hour
        Assert.Equal(days[0].FlightCost, days[0].TotalCost);
    }

    [Fact]
    public void A_sped_up_demo_flight_is_paid_for_its_time_in_the_air()
    {
        var at = At("2026-11-18T18:00:00Z");
        // 25.2 km at cruise speed is half an hour in the air; the demo played it back in a quarter of that.
        var demo = new Mission
        {
            Id = "d", CircuitId = "c1", DroneId = "D-1", Pilot = "Edna", Status = MissionStatus.Completed, CreatedAt = at,
            StartedAt = at, EndedAt = at + TimeSpan.FromMinutes(7.5), UpdatedAt = at + TimeSpan.FromMinutes(7.5),
            DistanceFlownMeters = GridSafety.Core.Simulation.FlightPlan.SpeedMps * 1800,
        };
        // Hovering over a tower: the logged time is longer than the distance needs, and counts.
        var hover = new Mission
        {
            Id = "h", CircuitId = "c1", DroneId = "D-1", Pilot = "Edna", Status = MissionStatus.Completed, CreatedAt = at,
            StartedAt = at, EndedAt = at + TimeSpan.FromHours(1), UpdatedAt = at + TimeSpan.FromHours(1), DistanceFlownMeters = 100,
        };

        Assert.Equal(0.5, FlightReports.Hours(demo), 3);
        Assert.Equal(1, FlightReports.Hours(hover), 3);
        var report = FlightReports.Of([demo, hover], _ => null);
        Assert.Equal(1.5, report.Totals.FlightHours, 2);
        Assert.Equal(1.5 * Costs.FlightCrewPerHour, report.Totals.CrewCost);
        Assert.Equal(30, report.Flights.Single(f => f.MissionId == "d").FlightMinutes, 1);
    }

    [Fact]
    public void Flight_log_days_add_up_to_the_totals()
    {
        var day1 = At("2026-11-18T18:00:00Z");
        var day2 = At("2026-11-20T02:00:00Z"); // still the 19th in California
        Mission Flown(string id, DateTimeOffset at, MissionStatus status) => new()
        {
            Id = id, CircuitId = "c1", DroneId = "D-1", Pilot = "Edna", Status = status, CreatedAt = at, UpdatedAt = at + TimeSpan.FromMinutes(30),
            StartedAt = at, EndedAt = at + TimeSpan.FromMinutes(30), DistanceFlownMeters = 1000,
        };

        var report = FlightReports.Of([Flown("a", day1, MissionStatus.Completed), Flown("b", day2, MissionStatus.Completed), Flown("c", day2, MissionStatus.Aborted)], _ => null);

        Assert.Equal([new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 19)], report.Days.Select(d => d.Day));
        Assert.Equal([1, 2], report.Days.Select(d => d.Missions));
        Assert.Equal(1, report.Days[1].Aborted);
        Assert.Equal(report.Totals.FlightHours, report.Days.Sum(d => d.FlightHours), 2);
        Assert.Equal(report.Totals.DistanceMeters, report.Days.Sum(d => d.DistanceMeters));
    }
}
