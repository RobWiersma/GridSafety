using GridSafety.Core;
using Xunit;

namespace GridSafety.Tests;

public class CrewRosterTests
{
    [Fact]
    public void Every_finding_type_has_a_crew_that_handles_it()
    {
        foreach (var type in Enum.GetValues<FindingType>())
            Assert.Contains(type, CrewRoster.SuggestFor(type).Handles);
    }

    [Fact]
    public void Roster_ids_and_names_are_unique()
    {
        var crews = CrewRoster.All;
        Assert.Equal(crews.Count, crews.Select(c => c.Id).Distinct().Count());
        var names = crews.Select(c => c.Name).Concat(crews.SelectMany(c => c.Members).Select(m => m.Name)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("hot-stick", "Springfield Hot-Stick Crew")]
    [InlineData("  springfield hot-stick crew ", "Springfield Hot-Stick Crew")]
    [InlineData("Lisa Simsen", "Lisa Simsen (Springfield Hot-Stick Crew)")]
    [InlineData("Lisa Simsen (Springfield Hot-Stick Crew)", "Lisa Simsen (Springfield Hot-Stick Crew)")]
    public void Resolve_accepts_ids_names_and_stored_labels(string text, string label) =>
        Assert.Equal(label, CrewRoster.Resolve(text)!.Label);

    [Fact]
    public void Every_technician_departs_from_a_known_depot()
    {
        foreach (var crew in CrewRoster.All)
            foreach (var m in crew.Members)
                Assert.Contains(CrewRoster.Depots, d => d.Id == crew.DepotOf(m));
    }

    [Fact]
    public void Candidates_are_qualified_on_duty_and_nearest_first()
    {
        var antioch = CrewRoster.Depots.Single(d => d.Id == "antioch").Location;
        var crews = CrewRoster.All.Select(c => c.Id == "sector-7g" ? c with { Status = CrewStatus.OffDuty } : c).ToList();

        var options = CrewRoster.Candidates(crews, CrewRoster.Depots, FindingType.CrossarmDamage, antioch);

        Assert.All(options, o => Assert.True(o.Crew.CanFix(FindingType.CrossarmDamage, o.Member)));
        Assert.DoesNotContain(options, o => o.Crew.Id == "sector-7g");
        var technicians = options.TakeWhile(o => o.Member is not null).ToList();
        Assert.Equal(["Barney Grumble"], technicians.Select(o => o.Member!.Name)); // the only on-duty tech cross-trained on crossarms
        Assert.All(options.Skip(technicians.Count), o => Assert.Null(o.Member));
    }

    [Fact]
    public void Nearest_technician_wins_when_several_qualify()
    {
        var richmond = CrewRoster.Depots.Single(d => d.Id == "richmond").Location;
        var options = CrewRoster.Candidates(CrewRoster.All, CrewRoster.Depots, FindingType.ConductorDamage, richmond);
        Assert.Equal("Lenny Leonardo", options[0].Member!.Name); // departs from the Richmond yard
        Assert.Equal(0, options[0].DistanceKm);
        Assert.Equal(options.Where(o => o.Member is not null).Select(o => o.DistanceKm).Order(), options.Where(o => o.Member is not null).Select(o => o.DistanceKm));
    }

    [Fact]
    public void Old_catalogs_get_depots_and_skills_when_read()
    {
        var old = new ResourceCatalog
        {
            GroundCrews =
            [
                .. CrewRoster.All.Select(c => c with { DepotId = null, Members = [.. c.Members.Select(m => m with { Skills = null, DepotId = null })] }),
                new Crew("custom", "Custom Crew", "Anything", [FindingType.ThermalHotspot], [new("Gil Gunderson", "Lineman")]),
            ],
        };
        old.Normalize();
        Assert.Equal(CrewRoster.Depots.Count, old.Depots.Count);
        Assert.Equal(CrewRoster.All.Select(c => c.DepotId), old.GroundCrews.Take(CrewRoster.All.Count).Select(c => c.DepotId));
        Assert.Equal("richmond", old.GroundCrews.Single(c => c.Id == "sector-7g").Members.Single(m => m.Name == "Lenny Leonardo").DepotId);
        Assert.Equal(CrewRoster.Depots[0].Id, old.GroundCrews.Single(c => c.Id == "custom").DepotId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Line Crew 4")]
    [InlineData("Lisa Simsen (Shelbyville Hardware Crew)")]
    public void Resolve_rejects_anyone_off_the_roster(string? text) => Assert.Null(CrewRoster.Resolve(text));
}
