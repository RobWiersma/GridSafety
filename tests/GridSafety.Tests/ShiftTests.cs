using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

public class ShiftTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 18, 0, 0, TimeSpan.Zero);
    private static readonly Crew Sector7G = CrewRoster.All.Single(c => c.Id == "sector-7g");
    private static readonly GeoPoint Richmond = CrewRoster.Depots.Single(d => d.Id == "richmond").Location;

    private static TimeSpan H(double simulatedHours) => Shifts.Sim(TimeSpan.FromHours(simulatedHours));

    /// <summary>A finished or planned truck roll for <paramref name="member"/> (null: the whole crew), <paramref name="hours"/> simulated hours long.</summary>
    private static FieldDispatch Job(string? member, DateTimeOffset start, double hours, string? next = null) => new()
    {
        Assignee = member ?? Sector7G.Name, CrewId = Sector7G.Id, Member = member,
        DepotId = "richmond", DepotName = "Richmond", Depot = Richmond, Site = Richmond, SiteLabel = "SA-1",
        From = Richmond, FromLabel = "Richmond", Route = [Richmond, Richmond], ReturnRoute = [Richmond, Richmond],
        DepartedAt = start, ArrivesAt = start + H(hours) / 3, RepairedAt = start + H(hours) * 2 / 3, ReturnsAt = start + H(hours), NextFindingId = next,
    };

    [Fact]
    public void Nobody_has_worked_yet_so_everyone_is_fresh()
    {
        var s = Shifts.Of([], Sector7G.Id, "Lenny Leonardo", T0);
        Assert.Equal(ShiftState.Fresh, s);
        Assert.Equal(Shifts.Limit, s.Left);
    }

    [Fact]
    public void Work_and_travel_count_as_they_happen_and_lined_up_jobs_are_booked_ahead()
    {
        var first = Job("Lenny Leonardo", T0, 3, next: "sa-2");
        var second = Job("Lenny Leonardo", first.ReturnsAt, 2); // straight on from the first
        var midway = first.ReturnsAt + H(1);

        var s = Shifts.Of([first, second], Sector7G.Id, "Lenny Leonardo", midway);
        Assert.Equal(T0, s.StartedAt);
        Assert.Equal(4, Shifts.Hours(s.Worked));
        Assert.Equal(5, Shifts.Hours(s.Booked));
        Assert.Equal(3, Shifts.Hours(s.Left));
        Assert.Null(s.RestingUntil);
    }

    [Fact]
    public void A_whole_crew_job_counts_for_every_member_and_the_crew_is_as_tight_as_its_busiest()
    {
        var crewJob = Job(null, T0, 2);
        var lennyAlone = Job("Lenny Leonardo", crewJob.ReturnsAt + H(1), 3);
        var now = lennyAlone.ReturnsAt;
        var hours = new FieldHours([crewJob, lennyAlone], now);

        Assert.Equal(2, Shifts.Hours(hours.Of(Sector7G, Sector7G.Members.Single(m => m.Name == "Carl Carlsen")).Booked));
        Assert.Equal(5, Shifts.Hours(hours.Of(Sector7G, Sector7G.Members.Single(m => m.Name == "Lenny Leonardo")).Booked));
        Assert.Equal(5, Shifts.Hours(hours.Of(Sector7G, null).Booked));
    }

    [Fact]
    public void After_a_full_shift_they_clock_out_and_rest_then_start_fresh()
    {
        var day = Job("Lenny Leonardo", T0, 6.5);
        var back = day.ReturnsAt;

        // Still driving back: on shift with 1.5 h left.
        Assert.Null(Shifts.Of([day], Sector7G.Id, "Lenny Leonardo", back - H(0.5)).RestingUntil);

        // Back at the depot with under 2 h left: done for the day.
        var resting = Shifts.Of([day], Sector7G.Id, "Lenny Leonardo", back + H(1));
        Assert.Equal(back + Shifts.Rest, resting.RestingUntil);
        Assert.Equal(TimeSpan.Zero, resting.Left);

        Assert.Equal(ShiftState.Fresh, Shifts.Of([day], Sector7G.Id, "Lenny Leonardo", back + Shifts.Rest));
    }

    [Fact]
    public void A_long_enough_break_starts_a_new_shift_even_without_a_full_day()
    {
        var morning = Job("Lenny Leonardo", T0, 3);
        var later = Job("Lenny Leonardo", morning.ReturnsAt + Shifts.Rest + H(1), 2);
        var s = Shifts.Of([morning, later], Sector7G.Id, "Lenny Leonardo", later.ReturnsAt);
        Assert.Equal(later.DepartedAt, s.StartedAt);
        Assert.Equal(2, Shifts.Hours(s.Booked));
    }

    [Fact]
    public void Resting_or_short_of_hours_people_are_not_offered_the_job()
    {
        var site = new GeoPoint(Richmond.Lat, Richmond.Lon + 10 / 88.0); // 10 km east of Richmond
        var lenny = Sector7G.Members.Single(m => m.Name == "Lenny Leonardo");

        // Lenny has 7 h booked (on a job elsewhere that's already done): a 10 km job needs more than the 1 h he has left.
        var long1 = Job("Lenny Leonardo", T0, 7);
        var now = long1.ReturnsAt - H(0.25);
        var offer = Pick(new FieldHours([long1], now, 3), site, lenny);
        Assert.True(offer.OutOfHours);
        Assert.False(offer.Available);
        Assert.True(Shifts.Hours(offer.JobTime!.Value) > Shifts.Hours(offer.Shift!.Left));

        // Once he's back, he's done for the day.
        offer = Pick(new FieldHours([long1], long1.ReturnsAt + H(0.1), 3), site, lenny);
        Assert.True(offer.Resting);
        Assert.False(offer.Available);

        // A fresh technician is offered it, with the rough job length.
        offer = Pick(new FieldHours([], now, 3), site, lenny);
        Assert.True(offer.Available);
        Assert.InRange(Shifts.Hours(offer.JobTime!.Value), 1, 4);
    }

    private static AssignCandidate Pick(FieldHours hours, GeoPoint site, CrewMember who) =>
        CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, FindingType.ConductorDamage, site, [], hours).Single(c => c.Member == who);
}
