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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Line Crew 4")]
    [InlineData("Lisa Simsen (Shelbyville Hardware Crew)")]
    public void Resolve_rejects_anyone_off_the_roster(string? text) => Assert.Null(CrewRoster.Resolve(text));
}
