using GridSafety.Core;

namespace GridSafety.Tests;

public class GeoTests
{
    private static readonly GeoPoint A = new(37.9405, -122.1620);

    [Fact]
    public void Haversine_matches_known_distance()
    {
        // ~1 km north
        var b = Geo.Offset(A, 1000, 0);
        Assert.InRange(Geo.DistanceMeters(A, b), 999, 1001);
    }

    [Fact]
    public void Distance_to_polyline_measures_perpendicular_offset()
    {
        var line = new[] { A, Geo.Offset(A, 0, 2000) };          // 2 km east-west line
        var p = Geo.Offset(A, 150, 1000);                       // 150 m north of its midpoint
        Assert.InRange(Geo.DistanceToPolylineMeters(p, line), 148, 152);
    }

    [Fact]
    public void Distance_to_polyline_clamps_to_endpoints()
    {
        var line = new[] { A, Geo.Offset(A, 0, 1000) };
        var beyondEnd = Geo.Offset(A, 0, 1300);
        Assert.InRange(Geo.DistanceToPolylineMeters(beyondEnd, line), 298, 302);
    }
}

public class CircuitCatalogTests
{
    [Fact]
    public void Seeded_circuits_have_realistic_spans()
    {
        foreach (var c in StaticCircuitCatalog.Instance.All)
        {
            Assert.True(c.Structures.Count >= 3, $"{c.Id} has too few structures");
            Assert.True(c.LengthMeters >= 400, $"{c.Id} is a substation stub");
            Assert.Equal(c.Structures.Count, c.Structures.Select(s => s.Id).Distinct().Count());
            var spans = c.Structures.Zip(c.Structures.Skip(1), (a, b) => Geo.DistanceMeters(a.Location, b.Location)).ToList();
            Assert.All(spans, s => Assert.InRange(s, 20, 1000)); // real tower spacing (230 kV spans reach ~800 m)
        }
    }

    [Fact]
    public void Lookup_is_case_insensitive() =>
        Assert.NotNull(StaticCircuitCatalog.Instance.Find("pitt-mart-115"));
}

public class WorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Circuit Circuit = TestCircuits.Line;

    [Fact]
    public void Mission_lifecycle_sets_timestamps_and_history()
    {
        var m = Mission.Plan(Circuit, " DRN-1 ", "Pat", "sup", Now);
        m.MoveTo(MissionStatus.InFlight, Now.AddMinutes(1));
        m.MoveTo(MissionStatus.Completed, Now.AddMinutes(20), "DRN-1", "landed");

        Assert.Equal("DRN-1", m.DroneId);
        Assert.Equal(Now.AddMinutes(1), m.StartedAt);
        Assert.Equal(Now.AddMinutes(20), m.EndedAt);
        Assert.Equal(2, m.History.Count);
        Assert.Empty(Mission.Workflow.Next(MissionStatus.Completed));
    }

    [Theory]
    [InlineData(MissionStatus.Planned, MissionStatus.Completed)]
    [InlineData(MissionStatus.Completed, MissionStatus.InFlight)]
    [InlineData(MissionStatus.Aborted, MissionStatus.InFlight)]
    public void Mission_rejects_illegal_moves(MissionStatus from, MissionStatus to)
    {
        var m = Mission.Plan(Circuit, "D", "P", null, Now);
        m.Status = from;
        Assert.Throws<InvalidTransitionException>(() => m.MoveTo(to, Now));
    }

    [Fact]
    public void Finding_assignment_requires_assignee()
    {
        var f = NewFinding(4);
        f.MoveTo(FindingStatus.Triaged, Now, "sup");
        Assert.Throws<ArgumentException>(() => f.MoveTo(FindingStatus.Assigned, Now, "sup", assignee: " "));

        f.MoveTo(FindingStatus.Assigned, Now, "sup", assignee: "Line Crew 4");
        Assert.Equal("Line Crew 4", f.AssignedTo);
        f.MoveTo(FindingStatus.Resolved, Now, "crew");
        Assert.True(Finding.Workflow.IsTerminal(f.Status));
    }

    [Fact]
    public void Only_dismissal_requires_supervisor()
    {
        Assert.True(Finding.RequiresSupervisor(FindingStatus.Dismissed));
        Assert.False(Finding.RequiresSupervisor(FindingStatus.Triaged));
        Assert.False(Finding.RequiresSupervisor(FindingStatus.Resolved));
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(4, 24)]
    [InlineData(3, 72)]
    public void Sla_window_tightens_with_severity(int severity, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), TriageSla.WindowFor(severity));

    [Fact]
    public void Low_severity_has_no_sla() => Assert.Null(TriageSla.WindowFor(2));

    [Fact]
    public void Overdue_only_while_untriaged()
    {
        var f = NewFinding(5, due: Now.AddHours(-1));
        Assert.True(f.IsOverdue(Now));
        f.MoveTo(FindingStatus.Triaged, Now, "sup");
        Assert.False(f.IsOverdue(Now));
    }

    internal static Finding NewFinding(int severity, DateTimeOffset? due = null, DateTimeOffset? created = null) => new()
    {
        Id = Ids.New(),
        MissionId = "m1",
        CircuitId = Circuit.Id,
        StructureId = Circuit.Structures[3].Id,
        StructureLabel = Circuit.Structures[3].Label,
        Type = FindingType.ThermalHotspot,
        Severity = severity,
        Confidence = 0.9,
        Location = Circuit.Structures[3].Location,
        CreatedAt = created ?? Now,
        UpdatedAt = created ?? Now,
        TriageDueAt = due,
    };
}

public class FlightRuleTests
{
    private static readonly Circuit Circuit = TestCircuits.Line;
    private static readonly DateTimeOffset T = DateTimeOffset.UtcNow;

    private static TelemetryPoint At(GeoPoint p, double alt = 60, double batt = 80) => new(T, p.Lat, p.Lon, alt, batt, 14);

    [Fact]
    public void Nominal_flight_raises_nothing() =>
        Assert.Empty(FlightRules.Evaluate(At(Circuit.Structures[5].Location), Circuit));

    [Fact]
    public void Detects_each_hazard()
    {
        var onLine = Circuit.Structures[5].Location;
        var kinds = (TelemetryPoint p) => FlightRules.Evaluate(p, Circuit).Select(a => a.Kind).ToList();

        Assert.Equal([AlertKind.LowBattery], kinds(At(onLine, batt: 25)));
        Assert.Equal([AlertKind.CriticalBattery], kinds(At(onLine, batt: 12)));
        Assert.Equal([AlertKind.AltitudeCeiling], kinds(At(onLine, alt: 130)));
        Assert.Equal([AlertKind.CorridorBreach], kinds(At(Geo.Offset(onLine, Circuit.CorridorMeters + 50, 0))));
    }

    [Fact]
    public void Sustained_condition_does_not_repage()
    {
        var current = FlightRules.Evaluate(At(Circuit.Structures[5].Location, alt: 130), Circuit);
        Assert.Single(FlightRules.NewlyRaised([], current));
        Assert.Empty(FlightRules.NewlyRaised([AlertKind.AltitudeCeiling], current));
    }

    [Fact]
    public void Low_battery_is_not_raised_after_critical()
    {
        var current = FlightRules.Evaluate(At(Circuit.Structures[5].Location, batt: 28), Circuit);
        Assert.Empty(FlightRules.NewlyRaised([AlertKind.CriticalBattery], current));
    }
}

public class OpsStatsTests
{
    [Fact]
    public void Computes_open_overdue_and_median_triage()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var f1 = WorkflowTests.NewFinding(5, due: now.AddHours(-1), created: now.AddHours(-5));   // overdue
        var f2 = WorkflowTests.NewFinding(3, created: now.AddHours(-10));
        f2.MoveTo(FindingStatus.Triaged, now.AddHours(-8), "sup");                                 // 2h to triage
        var f3 = WorkflowTests.NewFinding(4, created: now.AddHours(-10));
        f3.MoveTo(FindingStatus.Dismissed, now.AddHours(-6), "sup");                               // 4h to triage

        var stats = OpsStats.Compute([], [f1, f2, f3], [], now);

        Assert.Equal(2, stats.OpenFindings);
        Assert.Equal(1, stats.OpenBySeverity[5]);
        Assert.Equal(1, stats.OverdueTriage);
        Assert.Equal(3.0, stats.MedianHoursToTriage);
    }
}

public class OsmDatasetTests
{
    [Fact]
    public void Embedded_dataset_is_real_osm_with_attribution()
    {
        var data = StaticCircuitCatalog.Instance.Data;
        Assert.Contains("OpenStreetMap", data.Attribution);
        Assert.Contains("ODbL", data.License);
        Assert.True(data.Circuits.Count >= 15);
        Assert.Contains(data.Circuits, c => c.VoltageInferred);
        Assert.Contains(data.Circuits, c => c.VoltageKv == 0);
        Assert.All(data.Circuits, c =>
        {
            Assert.NotEmpty(c.OsmWayIds);
            Assert.All(c.Structures, s => Assert.True(s.OsmNodeId > 0));
            Assert.True(c.VoltageKv is 0 or (>= 50 and <= 500), $"{c.Id}: {c.VoltageKv} kV"); // 0 = not tagged in OSM
        });
    }

    [Fact]
    public void Structures_sit_inside_the_import_area()
    {
        var b = StaticCircuitCatalog.Instance.Data.Bbox;
        Assert.All(StaticCircuitCatalog.Instance.All.SelectMany(c => c.Structures), s =>
        {
            Assert.InRange(s.Location.Lat, b[0], b[2]);
            Assert.InRange(s.Location.Lon, b[1], b[3]);
        });
    }
}
