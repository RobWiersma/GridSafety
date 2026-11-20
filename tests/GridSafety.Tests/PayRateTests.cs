using GridSafety.Core;
using System.Globalization;
using Xunit;

namespace GridSafety.Tests;

public class PayRateTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-18T18:00:00Z", CultureInfo.InvariantCulture);

    private static readonly Crew Line = new("line", "Line Crew", "Line work", [FindingType.ConductorDamage],
        [new("Lenny", "Lineman", PayRate: 120), new("Carl", "Lineman")]);

    private static readonly FlightCrew Air = new("air", "Air Ops", "Concord yard",
        [new("Edna", FlightCrew.PilotRole, PayRate: 100), new("Seymour", FlightCrew.ObserverRole, PayRate: 60)]);

    /// <summary>A finished truck roll with these riders: one simulated hour (one real minute) out.</summary>
    private static Finding Job(params Rider[] riders) => JobAt(At, riders);

    private static Finding JobAt(DateTimeOffset at, params Rider[] riders)
    {
        var p = new GeoPoint(37.9, -122.0);
        return new Finding
        {
            Id = Ids.New(), MissionId = "m", CircuitId = "c", StructureId = "S-1", StructureLabel = "S-1", Type = FindingType.ConductorDamage,
            Severity = 3, Location = p, CreatedAt = At, UpdatedAt = At, Status = FindingStatus.Resolved,
            Dispatch = new FieldDispatch
            {
                Assignee = "Lenny (Line Crew)", CrewId = "line", Member = riders[0].Name, DepotId = "d", DepotName = "Yard", Depot = p, Site = p,
                SiteLabel = "S-1", From = p, FromLabel = "Yard", Route = [p], ReturnRoute = [p], Riders = riders,
                DepartedAt = at, ArrivesAt = at.AddSeconds(20), RepairedAt = at.AddSeconds(40), ReturnsAt = at.AddMinutes(1),
            },
        };
    }

    [Fact]
    public void Everyone_in_the_truck_is_costed_at_their_own_rate_or_the_standard_one()
    {
        var rates = new PayRates([Line], []);
        Assert.Equal(120, rates.Technician("line", "lenny"));
        Assert.Equal(Costs.LaborPerPersonHour, rates.Technician("line", "Carl"));

        var report = WorkReports.Of([Job(new("line", "Lenny"), new("line", "Carl"))], _ => true, _ => [], At.AddHours(1), rates: rates);
        Assert.Equal(2, report.Totals.PersonHours);
        Assert.Equal(120 + Costs.LaborPerPersonHour, report.Totals.LaborCost);
        Assert.Equal(report.Totals.LaborCost, report.Days.Single().LaborCost);

        var standard = WorkReports.Of([Job(new("line", "Lenny"), new("line", "Carl"))], _ => true, _ => [], At.AddHours(1));
        Assert.Equal(2 * Costs.LaborPerPersonHour, standard.Totals.LaborCost);
    }

    [Fact]
    public void A_raise_applies_from_when_it_is_given_and_past_work_keeps_the_old_rate()
    {
        var raisedAt = At.AddHours(1);
        var lenny = PayRates.Change(Line.Members[0], 150, raisedAt);
        Assert.Equal([new PayChange(DateTimeOffset.MinValue, 120), new PayChange(raisedAt, 150)], lenny.PayHistory!);
        Assert.Same(lenny, PayRates.Change(lenny, 150, raisedAt.AddHours(1))); // no change, nothing recorded

        // Carl goes from the standard rate to his own and back.
        var carl = PayRates.Change(PayRates.Change(Line.Members[1], 110, raisedAt), null, raisedAt.AddHours(2));
        Assert.Equal([null, 110, null], new[] { At, raisedAt, raisedAt.AddHours(3) }.Select(t => PayRates.OwnRateAt(carl, t)));

        var rates = new PayRates([Line with { Members = [lenny, carl] }], []);
        Assert.Equal((120.0, 150.0, 150.0), (rates.Technician("line", "Lenny", At), rates.Technician("line", "Lenny", raisedAt), rates.Technician("line", "Lenny")));
        var before = JobAt(At, new Rider("line", "Lenny"));
        var after = JobAt(raisedAt.AddHours(1), new Rider("line", "Lenny"), new Rider("line", "Carl"));
        var report = WorkReports.Of([before, after], _ => true, _ => [], raisedAt.AddDays(1), rates: rates);
        Assert.Equal(120 + 150 + 110, report.Totals.LaborCost);
    }

    [Fact]
    public void A_flight_is_costed_at_its_pilots_rate_and_their_crews_observer()
    {
        var rates = new PayRates([], [Air]);
        Assert.Equal(160, rates.Flight("edna"));
        Assert.Equal(Costs.FlightCrewPerHour, rates.Flight("Someone else"));
        Assert.Equal(Costs.FlightCrewPerHour, Costs.PilotPerHour + Costs.ObserverPerHour);

        var flight = new Mission
        {
            Id = "m", CircuitId = "c", DroneId = "D-1", Pilot = "Edna", Status = MissionStatus.Completed, CreatedAt = At,
            StartedAt = At, EndedAt = At.AddHours(1), UpdatedAt = At.AddHours(1),
        };
        Assert.Equal(160, FlightReports.Of([flight], _ => null, rates: rates).Totals.CrewCost);

        // A pilot's raise after the flight doesn't reprice it.
        var raised = new PayRates([], [Air with { Members = [PayRates.Change(Air.Members[0], 130, At.AddHours(2)), Air.Members[1]] }]);
        Assert.Equal((160.0, 190.0), (raised.Flight(flight), raised.Flight("Edna")));

        var day = ActivityReports.Daily([Job(new Rider("line", "Lenny"))], [flight], At.AddHours(2), rates: new PayRates([Line], [Air])).Single();
        Assert.Equal((120.0, 160.0), (day.LaborCost, day.FlightCost));
        Assert.Equal(280, day.TotalCost);
    }
}
