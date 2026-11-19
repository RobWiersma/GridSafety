namespace GridSafety.Core;

/// <summary>
/// One person on a crew. For ground crews, <paramref name="Skills"/> are the issue types this technician is qualified
/// to fix (null: whatever the crew handles) and <paramref name="DepotId"/> is where they start the day (null: the crew's depot).
/// </summary>
public sealed record CrewMember(string Name, string Role, IReadOnlyList<FindingType>? Skills = null, string? DepotId = null);

public enum CrewStatus { Available, OffDuty }

/// <summary>
/// A ground (field) crew that work orders (assigned findings) go to. <see cref="Handles"/> is what the crew as a whole is
/// qualified for; members may be qualified for more or less. <see cref="DepotId"/> is the yard the crew rolls out of.
/// </summary>
public sealed record Crew(string Id, string Name, string Specialty, IReadOnlyList<FindingType> Handles, IReadOnlyList<CrewMember> Members,
    CrewStatus Status = CrewStatus.Available, string? DepotId = null)
{
    /// <summary>What this member can fix: their own skills, or the crew's when they have none listed.</summary>
    public IReadOnlyList<FindingType> SkillsOf(CrewMember m) => m.Skills ?? Handles;

    /// <summary>Where this member departs from: their own depot, or the crew's.</summary>
    public string? DepotOf(CrewMember m) => m.DepotId ?? DepotId;

    /// <summary>Whether the crew, or one member of it, is qualified for this kind of defect.</summary>
    public bool CanFix(FindingType type, CrewMember? member = null) =>
        member is null ? Handles.Contains(type) : SkillsOf(member).Contains(type);
}

/// <summary>A yard that ground crews and technicians depart from.</summary>
public sealed record Depot(string Id, string Name, GeoPoint Location);

/// <summary>
/// Someone who could take a work order: a technician, or a whole crew (<see cref="Member"/> null), with the depot
/// they leave from and the straight-line distance from there to the defect.
/// </summary>
/// <remarks>
/// <see cref="After"/> is set when they're already out nearby and would come here after their last job lined up; the
/// distance is then from that job. <see cref="BusyUntil"/> is set when they're out elsewhere and can't take this one.
/// <see cref="Shift"/> (when hours are tracked) says how much of their shift is left; <see cref="JobTime"/> is roughly how
/// much of it this job would take. Someone resting, or without enough hours left for the job, can't take it.
/// </remarks>
public sealed record AssignCandidate(Crew Crew, CrewMember? Member, Depot? Depot, double? DistanceKm, DateTimeOffset? BusyUntil = null,
    FieldDispatch? After = null, ShiftState? Shift = null, TimeSpan? JobTime = null)
{
    public bool Resting => Shift?.RestingUntil is not null;
    public bool OutOfHours => !Resting && Shift is { } s && JobTime is { } job && job > s.Left;
    public bool Available => BusyUntil is null && !Resting && !OutOfHours;

    public string Label => new CrewAssignee(Crew, Member).Label;
}

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
    /// <summary>The demo's yards. Points are rough town centres, not real facilities.</summary>
    public static readonly IReadOnlyList<Depot> Depots =
    [
        new("concord", "Concord yard", new(37.9780, -122.0311)),
        new("walnut-creek", "Walnut Creek yard", new(37.9101, -122.0652)),
        new("martinez", "Martinez yard", new(38.0194, -122.1341)),
        new("pittsburg", "Pittsburg yard", new(38.0280, -121.8847)),
        new("antioch", "Antioch yard", new(38.0049, -121.8058)),
        new("san-ramon", "San Ramon yard", new(37.7799, -121.9780)),
        new("richmond", "Richmond yard", new(37.9358, -122.3478)),
    ];

    public static readonly IReadOnlyList<Crew> All =
    [
        new("sector-7g", "Sector 7G Line Crew", "Conductors and crossarms",
            [FindingType.ConductorDamage, FindingType.CrossarmDamage],
            [
                new("Homer Simsen", "Foreman"),
                new("Lenny Leonardo", "Journeyman lineman", [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.MissingHardware], "richmond"),
                new("Carl Carlsen", "Journeyman lineman", [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.DamagedInsulator], "martinez"),
            ],
            DepotId: "concord"),
        new("evergreen", "Evergreen Terrace Vegetation Crew", "Tree trimming and clearance",
            [FindingType.VegetationEncroachment],
            [
                new("Willie MacTavish", "Lead arborist"),
                new("Cletus Spuckley", "Climber", DepotId: "antioch"),
                new("Ned Flandry", "Groundman", DepotId: "san-ramon"),
            ],
            DepotId: "walnut-creek"),
        new("shelbyville", "Shelbyville Hardware Crew", "Insulators and hardware",
            [FindingType.DamagedInsulator, FindingType.MissingHardware],
            [
                new("Moe Sizlack", "Foreman"),
                new("Barney Grumble", "Lineman", [FindingType.DamagedInsulator, FindingType.MissingHardware, FindingType.CrossarmDamage], "san-ramon"),
                new("Otto Mannheim", "Apprentice lineman", [FindingType.MissingHardware]),
            ],
            DepotId: "pittsburg"),
        new("hot-stick", "Springfield Hot-Stick Crew", "Thermal repairs on energized lines",
            [FindingType.ThermalHotspot],
            [
                new("Frank Grimsby", "Foreman", [FindingType.ThermalHotspot, FindingType.ConductorDamage]),
                new("Lisa Simsen", "Thermographer", DepotId: "walnut-creek"),
                new("Marjorie Bouvay", "Hot-stick lineman", DepotId: "richmond"),
            ],
            DepotId: "antioch"),
    ];

    /// <summary>The crew that normally handles this kind of defect.</summary>
    public static Crew SuggestFor(FindingType type) => SuggestFor(All, type)!;

    /// <summary>The first on-duty crew that handles this defect type (any crew that handles it, failing that); null for an empty roster.</summary>
    public static Crew? SuggestFor(IReadOnlyList<Crew> crews, FindingType type) =>
        crews.FirstOrDefault(c => c.Status == CrewStatus.Available && c.Handles.Contains(type))
        ?? crews.FirstOrDefault(c => c.Handles.Contains(type))
        ?? crews.FirstOrDefault();

    /// <summary>
    /// Who can take a work order for <paramref name="type"/> at <paramref name="site"/>: on-duty technicians qualified for
    /// it, then on-duty crews that handle it as a unit, each list nearest depot first. People without a known depot go last.
    /// Someone already out (<paramref name="active"/>) can take it after their current jobs when it's in the same area,
    /// measured from their last job; otherwise they're listed after everyone available, marked busy.
    /// </summary>
    public static IReadOnlyList<AssignCandidate> Candidates(IReadOnlyList<Crew> crews, IReadOnlyList<Depot> depots, FindingType type, GeoPoint site,
        IReadOnlyList<FieldDispatch>? active = null, FieldHours? hours = null)
    {
        AssignCandidate Make(Crew c, CrewMember? m)
        {
            var depot = depots.FirstOrDefault(d => d.Id == (m is null ? c.DepotId : c.DepotOf(m)));
            var label = new CrewAssignee(c, m).Label;
            var busy = active is null ? null : FieldWork.BusyUntil(active, c.Id, m?.Name);
            var after = busy is not null && FieldWork.CanChain(active!, label, site) ? FieldWork.Tail(active!, label) : null;
            GeoPoint? start = after?.Site ?? depot?.Location;
            var shift = hours?.Of(c, m);
            var job = hours is not null && start is { } from && depot is not null ? Shifts.Estimate(from, site, depot.Location, hours.Severity, after) : (TimeSpan?)null;
            return new(c, m, depot, start is { } p ? Math.Round(Geo.DistanceMeters(p, site) / 1000, 1) : null, after is null ? busy : null, after, shift, job);
        }
        static IEnumerable<AssignCandidate> Nearest(IEnumerable<AssignCandidate> xs) =>
            xs.OrderBy(x => !x.Available).ThenBy(x => x.DistanceKm ?? double.MaxValue).ThenBy(x => x.Crew.Name).ThenBy(x => x.Member?.Name);

        var onDuty = crews.Where(c => c.Status == CrewStatus.Available).ToList();
        var technicians = onDuty.SelectMany(c => c.Members.Where(m => c.CanFix(type, m)).Select(m => Make(c, m)));
        var wholeCrews = onDuty.Where(c => c.CanFix(type)).Select(c => Make(c, null));
        return [.. Nearest(technicians), .. Nearest(wholeCrews)];
    }

    /// <summary>
    /// Matches a crew id, crew name, member name, or a stored "Member (Crew)" label, ignoring case.
    /// Returns null when the text names nobody on the roster.
    /// </summary>
    public static CrewAssignee? Resolve(string? text) => Resolve(All, text);

    /// <inheritdoc cref="Resolve(string?)"/>
    public static CrewAssignee? Resolve(IReadOnlyList<Crew> crews, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        foreach (var crew in crews)
        {
            if (Same(t, crew.Id) || Same(t, crew.Name)) return new(crew, null);
            foreach (var m in crew.Members)
                if (Same(t, m.Name) || Same(t, $"{m.Name} ({crew.Name})")) return new(crew, m);
        }
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
