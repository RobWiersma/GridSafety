using System.Xml.Linq;
using GridSafety.Core;

namespace GridSafety.Tests;

public class FindingImageTests
{
    private static Finding Make(FindingType type, int severity = 4, string id = "f-123") => new()
    {
        Id = id,
        MissionId = "m1",
        CircuitId = "C1",
        StructureId = "C1-S1",
        StructureLabel = "LM-023",
        Type = type,
        Severity = severity,
        Confidence = 0.74,
        Location = new GeoPoint(37.91984, -122.02448),
        Description = "test",
        CreatedAt = new DateTimeOffset(2026, 11, 16, 18, 35, 12, TimeSpan.Zero),
    };

    public static TheoryData<FindingType> Types() => [.. Enum.GetValues<FindingType>()];

    [Theory]
    [MemberData(nameof(Types))]
    public void Every_type_renders_valid_svg_with_its_label_and_simulated_badge(FindingType type)
    {
        var svg = FindingImage.Svg(Make(type));

        var root = XDocument.Parse(svg).Root!;
        Assert.Equal("svg", root.Name.LocalName);
        Assert.Contains(FindingImage.Label(type), svg, StringComparison.Ordinal);
        Assert.Contains("SIMULATED", svg, StringComparison.Ordinal);
        Assert.Contains("LM-023", svg, StringComparison.Ordinal);
        Assert.Contains("37.91984, -122.02448", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_finding_always_gets_the_same_picture_and_different_findings_vary()
    {
        var a = FindingImage.Svg(Make(FindingType.VegetationEncroachment, id: "abc"));
        Assert.Equal(a, FindingImage.Svg(Make(FindingType.VegetationEncroachment, id: "abc")));
        Assert.NotEqual(a, FindingImage.Svg(Make(FindingType.VegetationEncroachment, id: "xyz")));
    }

    [Fact]
    public void Text_from_the_finding_is_escaped()
    {
        var f = Make(FindingType.MissingHardware);
        var svg = FindingImage.Svg(new Finding
        {
            Id = f.Id, MissionId = f.MissionId, CircuitId = f.CircuitId, StructureId = f.StructureId,
            StructureLabel = "<script>x</script>", Type = f.Type, Severity = f.Severity, Location = f.Location,
        });
        Assert.DoesNotContain("<script>", svg, StringComparison.Ordinal);
        XDocument.Parse(svg);
    }

    /// <summary>Set GRIDSAFETY_IMAGE_DIR to write one sample per type and severity for a visual check.</summary>
    [Fact]
    public void Writes_samples_when_asked()
    {
        if (Environment.GetEnvironmentVariable("GRIDSAFETY_IMAGE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var type in Enum.GetValues<FindingType>())
            foreach (var sev in new[] { 2, 5 })
                File.WriteAllText(Path.Combine(dir, $"{type}-{sev}.svg"), FindingImage.Svg(Make(type, sev, $"{type}{sev}")));
    }
}
