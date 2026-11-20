using GridSafety.Core;
using GridSafety.Core.Simulation;
using Xunit;

namespace GridSafety.Tests;

public class FlightLegsTests
{
    private static readonly GeoPoint Origin = new(38.0, -122.15);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>A straight line of <paramref name="spans"/> spans of <paramref name="spanMeters"/>.</summary>
    private static Circuit Line(int spans, double spanMeters = 300) => new("LONG", "Long line", 230, 60,
        [.. Enumerable.Range(0, spans + 1).Select(i => new Structure($"LONG-{i:000}", $"L-{i:000}", Geo.Offset(Origin, 0, i * spanMeters)))]);

    [Fact]
    public void A_flight_covers_about_15_km_of_line_on_a_20_minute_battery()
    {
        Assert.InRange(FlightLegs.MaxLegMeters, 15_000, 15_200);
        Assert.Equal(20, FlightLegs.MinutesFor(FlightLegs.MaxLegMeters), 1);
        // A full flight lands with about a fifth of the battery left.
        Assert.InRange(FlightPlan.BatteryAfter(FlightLegs.Endurance - TimeSpan.FromMinutes(2)), 24, 28);
    }

    [Fact]
    public void A_short_line_is_one_flight()
    {
        var legs = TestCircuits.Line.Legs();
        var leg = Assert.Single(legs);
        Assert.Equal((1, 1, 0, 11), (leg.Number, leg.Of, leg.FirstIndex, leg.LastIndex));
        Assert.Equal("T-001", leg.FromLabel);
        Assert.Equal("T-012", leg.ToLabel);
        Assert.Same(TestCircuits.Line, TestCircuits.Line.Stretch(null));
    }

    [Fact]
    public void A_long_line_is_split_into_near_equal_flights_that_each_fit_on_a_battery()
    {
        var line = Line(200); // 60 km
        var legs = line.Legs();

        Assert.Equal(4, legs.Count);
        Assert.All(legs, l => Assert.True(l.LengthMeters <= FlightLegs.MaxLegMeters, $"flight {l.Number} is {l.LengthMeters} m"));
        Assert.All(legs, l => Assert.InRange(l.LengthMeters, 14_000, 16_000));
        Assert.All(legs, l => Assert.True(l.Minutes <= 20));
        // End to end, each flight starting where the last one landed.
        Assert.Equal(0, legs[0].FirstIndex);
        Assert.Equal(200, legs[^1].LastIndex);
        Assert.All(legs.Zip(legs.Skip(1)), p => Assert.Equal(p.First.LastIndex, p.Second.FirstIndex));
        Assert.Equal([1, 2, 3, 4], legs.Select(l => l.Number));
        Assert.All(legs, l => Assert.Equal(4, l.Of));

        var second = line.Stretch(2);
        Assert.Equal(line.Id, second.Id);
        Assert.Equal(legs[1].Structures, second.Structures.Count);
        Assert.Equal(line.Structures[legs[1].FirstIndex], second.Structures[0]);
    }

    [Fact]
    public void Every_real_line_splits_into_flights_that_fit_on_a_battery()
    {
        foreach (var c in StaticCircuitCatalog.Instance.All.Where(c => c.Structures.Count >= 2))
        {
            var legs = c.Legs();
            Assert.Equal(c.Structures.Count - 1, legs.Sum(l => l.LastIndex - l.FirstIndex));
            Assert.All(legs, l => Assert.True(l.LengthMeters <= FlightLegs.MaxLegMeters || l.LastIndex == l.FirstIndex + 1,
                $"{c.Id} flight {l.Number}: {l.LengthMeters} m"));
        }
    }

    [Fact]
    public void The_next_flight_is_one_never_flown_then_the_one_inspected_longest_ago_skipping_any_in_the_air()
    {
        var line = Line(200);
        Mission Flown(int leg, int hoursAgo, MissionStatus status = MissionStatus.Completed)
        {
            var m = Mission.Plan(line, "D", "P", null, Now.AddHours(-hoursAgo - 1), leg: leg);
            if (status == MissionStatus.Planned) return m;
            m.MoveTo(MissionStatus.InFlight, Now.AddHours(-hoursAgo - 1));
            if (status != MissionStatus.InFlight) m.MoveTo(status, Now.AddHours(-hoursAgo));
            return m;
        }

        Assert.Equal(1, line.NextLeg([]));
        Assert.Equal(3, line.NextLeg([Flown(1, 5), Flown(2, 4)]));
        Assert.Equal(2, line.NextLeg([Flown(1, 5), Flown(2, 9), Flown(3, 3), Flown(4, 2)]));
        // An aborted flight doesn't count as inspected; one in the air is left to finish.
        Assert.Equal(1, line.NextLeg([Flown(1, 5, MissionStatus.Aborted), Flown(2, 4), Flown(3, 3), Flown(4, 2)]));
        Assert.Equal(2, line.NextLeg([Flown(1, 0, MissionStatus.InFlight), Flown(2, 4), Flown(3, 3), Flown(4, 2)]));
    }

    [Fact]
    public void Coverage_is_measured_against_the_flight_not_the_whole_line()
    {
        var line = Line(200);
        var m = Mission.Plan(line, "D", "P", null, Now, leg: 2);
        Assert.Equal((2, 4), (m.Leg, m.Legs));
        m.StructuresInspected.AddRange(line.Stretch(2).Structures.Select(s => s.Id));
        m.StructuresInspected.Add(line.Structures[0].Id); // passed one outside the stretch: doesn't count
        Assert.Equal(1, m.Coverage(line));

        // Missions from before lines were split covered the whole line.
        var old = new Mission { Id = "old", CircuitId = line.Id, DroneId = "D", Pilot = "P" };
        old.StructuresInspected.AddRange(line.Stretch(2).Structures.Select(s => s.Id));
        Assert.InRange(old.Coverage(line), 0.2, 0.3);
    }

    [Fact]
    public void A_simulated_flight_over_one_stretch_drains_the_battery_by_time_in_the_air()
    {
        var stretch = Line(200).Stretch(1);
        var plan = FlightPlan.Build(stretch, new Random(3), scriptedIncidents: false, Now);
        var minutes = plan.Points.Count * FlightPlan.SecondsPerTick / 60;

        Assert.InRange(minutes, 16, 18.5);
        Assert.InRange(plan.Points[0].BatteryPct, 95, 97);
        Assert.InRange(plan.Points[^1].BatteryPct, 24, 33);
        Assert.InRange(Geo.DistanceMeters(plan.Points[^1].Position, stretch.Structures[^1].Location), 0, 20);

        var shortHop = FlightPlan.Build(TestCircuits.Line, new Random(3), scriptedIncidents: false, Now);
        Assert.True(shortHop.Points[^1].BatteryPct > 80, "a 3 km line barely dents the battery");
    }
}
