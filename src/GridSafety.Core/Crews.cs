namespace GridSafety.Core;

public sealed record CrewMember(string Name, string Role);

/// <summary>A field crew that work orders (assigned findings) go to. <see cref="Handles"/> drives the suggested crew.</summary>
public sealed record Crew(string Id, string Name, string Specialty, IReadOnlyList<FindingType> Handles, IReadOnlyList<CrewMember> Members);

/// <summary>Who an assignee string points at: a whole crew, or one member of it.</summary>
public sealed record CrewAssignee(Crew Crew, CrewMember? Member)
{
    /// <summary>The text stored on the finding: the crew name, or "Member (Crew)".</summary>
    public string Label => Member is null ? Crew.Name : $"{Member.Name} ({Crew.Name})";
}

/// <summary>
/// The demo's fixed roster. Everyone here is fictional (names riff on a certain cartoon town);
/// a real deployment would load crews from the workforce-management system.
/// </summary>
public static class CrewRoster
{
    public static readonly IReadOnlyList<Crew> All =
    [
        new("sector-7g", "Sector 7G Line Crew", "Conductors and crossarms",
            [FindingType.ConductorDamage, FindingType.CrossarmDamage],
            [new("Homer Simsen", "Foreman"), new("Lenny Leonardo", "Journeyman lineman"), new("Carl Carlsen", "Journeyman lineman")]),
        new("evergreen", "Evergreen Terrace Vegetation Crew", "Tree trimming and clearance",
            [FindingType.VegetationEncroachment],
            [new("Willie MacTavish", "Lead arborist"), new("Cletus Spuckley", "Climber"), new("Ned Flandry", "Groundman")]),
        new("shelbyville", "Shelbyville Hardware Crew", "Insulators and hardware",
            [FindingType.DamagedInsulator, FindingType.MissingHardware],
            [new("Moe Sizlack", "Foreman"), new("Barney Grumble", "Lineman"), new("Otto Mannheim", "Apprentice lineman")]),
        new("hot-stick", "Springfield Hot-Stick Crew", "Thermal repairs on energized lines",
            [FindingType.ThermalHotspot],
            [new("Frank Grimsby", "Foreman"), new("Lisa Simsen", "Thermographer"), new("Marjorie Bouvay", "Hot-stick lineman")]),
    ];

    /// <summary>The crew that normally handles this kind of defect.</summary>
    public static Crew SuggestFor(FindingType type) => All.FirstOrDefault(c => c.Handles.Contains(type)) ?? All[0];

    /// <summary>
    /// Matches a crew id, crew name, member name, or a stored "Member (Crew)" label, ignoring case.
    /// Returns null when the text names nobody on the roster.
    /// </summary>
    public static CrewAssignee? Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        foreach (var crew in All)
        {
            if (Same(t, crew.Id) || Same(t, crew.Name)) return new(crew, null);
            foreach (var m in crew.Members)
                if (Same(t, m.Name) || Same(t, $"{m.Name} ({crew.Name})")) return new(crew, m);
        }
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
