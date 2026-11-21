namespace GridSafety.Core;

/// <summary>
/// One person on a crew. For ground crews, <paramref name="Skills"/> are the issue types this technician is qualified
/// to fix (null: whatever the crew handles) and <paramref name="DepotId"/> is where they start the day (null: the crew's depot).
/// <paramref name="PayRate"/> is their own hourly rate, set by a supervisor (null: the standard rate for their role, see
/// <see cref="Costs"/> and <see cref="PayRates"/>). <paramref name="Profile"/> is their employee record (contact details and such).
/// <paramref name="PayHistory"/> is every change to their rate, oldest first, so past work is costed at the rate it was done at.
/// </summary>
public sealed record CrewMember(string Name, string Role, IReadOnlyList<FindingType>? Skills = null, string? DepotId = null, double? PayRate = null,
    EmployeeProfile? Profile = null, IReadOnlyList<PayChange>? PayHistory = null);

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
/// When trucks are tracked, someone leaving the yard needs a free one: <see cref="Vehicle"/> is the truck they'd take and
/// <see cref="Partner"/> the technician from the same yard (their own crew first) who'd ride along; <see cref="NoVehicle"/> is set when every
/// truck at their yard is out (<see cref="VehicleBackAt"/>: when the next one is back).
/// </remarks>
public sealed record AssignCandidate(Crew Crew, CrewMember? Member, Depot? Depot, double? DistanceKm, DateTimeOffset? BusyUntil = null,
    FieldDispatch? After = null, ShiftState? Shift = null, TimeSpan? JobTime = null, Vehicle? Vehicle = null, CrewAssignee? Partner = null,
    bool NoVehicle = false, DateTimeOffset? VehicleBackAt = null)
{
    public bool Resting => Shift?.RestingUntil is not null;
    public bool OutOfHours => !Resting && Shift is { } s && JobTime is { } job && job > s.Left;
    public bool Available => BusyUntil is null && !Resting && !OutOfHours && !NoVehicle;

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
        // Alameda County
        new("oakland", "Oakland yard", new(37.8044, -122.2712)),
        new("hayward", "Hayward yard", new(37.6688, -122.0808)),
        new("fremont", "Fremont yard", new(37.5485, -121.9886)),
        new("livermore", "Livermore yard", new(37.6819, -121.7680)),
        // Solano County
        new("vallejo", "Vallejo yard", new(38.1041, -122.2566)),
        new("fairfield", "Fairfield yard", new(38.2494, -122.0400)),
        new("vacaville", "Vacaville yard", new(38.3566, -121.9877)),
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
        new("capital-city", "Capital City District Crew", "Alameda County line work",
            [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.DamagedInsulator, FindingType.MissingHardware],
            [
                new("Lou Kowalski", "Foreman"),
                new("Eddie Lindqvist", "Journeyman lineman", [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.MissingHardware], "oakland"),
                new("Gil Gunderson", "Lineman", [FindingType.DamagedInsulator, FindingType.MissingHardware], "fremont"),
                new("Luann Van Houten", "Thermographer", [FindingType.ThermalHotspot], "livermore"),
                new("Sideshow Mel", "Arborist", [FindingType.VegetationEncroachment], "fremont"),
            ],
            DepotId: "hayward"),
        new("ogdenville", "Ogdenville District Crew", "Solano County line work",
            [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.DamagedInsulator, FindingType.MissingHardware],
            [
                new("Cookie Kwan", "Foreman", DepotId: "vallejo"),
                new("Disco Stu", "Journeyman lineman", [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.ThermalHotspot], "vacaville"),
                new("Agnes Skinley", "Lineman", [FindingType.DamagedInsulator, FindingType.MissingHardware, FindingType.CrossarmDamage]),
                new("Jasper Beardly", "Arborist", [FindingType.VegetationEncroachment], "vacaville"),
            ],
            DepotId: "fairfield"),
    ];

    /// <summary>
    /// A crew at each yard that has fewer people than its trucks have seats, staffed to fill them: everyone already departing
    /// from the yard (whatever their crew) counts, and new hires make up the rest. Each yard crew is led by a foreman and has
    /// a mix of skills, so every yard can take every kind of defect. Fictional names; deterministic, so the same catalog
    /// always gets the same people.
    /// </summary>
    public static IReadOnlyList<Crew> YardCrews(IReadOnlyList<Depot> depots, IReadOnlyList<Vehicle> vehicles, IReadOnlyList<Crew> crews)
    {
        ArgumentNullException.ThrowIfNull(depots);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(crews);
        var taken = crews.SelectMany(c => c.Members).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        string NewName()
        {
            while (true)
            {
                var (first, last) = (next % FirstNames.Length, (next % FirstNames.Length + next / FirstNames.Length) % LastNames.Length);
                next++;
                var name = $"{FirstNames[first]} {LastNames[last]}";
                if (taken.Add(name)) return name;
            }
        }

        var yardCrews = new List<Crew>();
        foreach (var depot in depots)
        {
            var seats = vehicles.Where(v => v.DepotId == depot.Id).Sum(v => v.Seats);
            var staffed = crews.Sum(c => c.Members.Count(m => c.DepotOf(m) == depot.Id));
            var hires = seats - staffed;
            if (hires <= 0 || crews.Any(c => c.Id == YardCrewId(depot))) continue;
            var members = Enumerable.Range(0, hires).Select(i => i == 0
                ? new CrewMember(NewName(), "Foreman", [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.DamagedInsulator, FindingType.MissingHardware])
                : new CrewMember(NewName(), YardRoles[(i - 1) % YardRoles.Length].Role, YardRoles[(i - 1) % YardRoles.Length].Skills)).ToList();
            var place = depot.Name.EndsWith(" yard", StringComparison.OrdinalIgnoreCase) ? depot.Name[..^5] : depot.Name;
            yardCrews.Add(new Crew(YardCrewId(depot), $"{place} Yard Crew", $"General line work out of the {depot.Name}",
                Enum.GetValues<FindingType>(), members, DepotId: depot.Id));
        }
        return yardCrews;
    }

    private static string YardCrewId(Depot depot) => $"yard-{depot.Id}";

    /// <summary>The roles a yard crew is made of after its foreman, in hiring order, so even a small crew covers the common defects.</summary>
    private static readonly (string Role, FindingType[] Skills)[] YardRoles =
    [
        ("Journeyman lineman", [FindingType.ConductorDamage, FindingType.CrossarmDamage, FindingType.MissingHardware]),
        ("Arborist", [FindingType.VegetationEncroachment]),
        ("Lineman", [FindingType.DamagedInsulator, FindingType.MissingHardware, FindingType.CrossarmDamage]),
        ("Thermographer", [FindingType.ThermalHotspot]),
        ("Groundman", [FindingType.VegetationEncroachment, FindingType.MissingHardware]),
        ("Hot-stick lineman", [FindingType.ThermalHotspot, FindingType.ConductorDamage]),
        ("Apprentice lineman", [FindingType.MissingHardware]),
    ];

    private static readonly string[] FirstNames =
        ["Ana", "Ben", "Carmen", "Dev", "Elena", "Felix", "Grace", "Hiro", "Ines", "Jamal", "Kara", "Luis",
         "Mei", "Nate", "Olivia", "Priya", "Quinn", "Rosa", "Sam", "Tomas", "Uma", "Victor", "Wendy", "Yusuf"];

    private static readonly string[] LastNames =
        ["Alvarez", "Brooks", "Chen", "Delgado", "Evans", "Fischer", "Garcia", "Hayes", "Ibarra", "Jensen", "Kim", "Lopez",
         "Moreno", "Nguyen", "Okafor", "Patel", "Quintero", "Reyes", "Singh", "Torres", "Usman", "Vasquez", "Walsh", "Young"];

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
    /// Someone already out (<paramref name="active"/>) can take it after their truck's current jobs when it's in the same
    /// area, measured from its last stop, whether they're doing that repair or riding along; otherwise they're listed after
    /// everyone available, marked busy. With <paramref name="vehicles"/>, someone leaving the yard takes a free truck there
    /// (none free: not available) and, when it has room, an on-duty technician from the same yard rides along: one from
    /// their own crew if there is one, then whoever has the most hours left.
    /// </summary>
    public static IReadOnlyList<AssignCandidate> Candidates(IReadOnlyList<Crew> crews, IReadOnlyList<Depot> depots, FindingType type, GeoPoint site,
        IReadOnlyList<FieldDispatch>? active = null, FieldHours? hours = null, IReadOnlyList<Vehicle>? vehicles = null)
    {
        var onDuty = crews.Where(c => c.Status == CrewStatus.Available).ToList();
        AssignCandidate Make(Crew c, CrewMember? m)
        {
            var depot = depots.FirstOrDefault(d => d.Id == (m is null ? c.DepotId : c.DepotOf(m)));
            var busy = active is null ? null : FieldWork.BusyUntil(active, c.Id, m?.Name);
            var tail = busy is null ? null : FieldWork.Tail(active!, c.Id, m?.Name);
            var after = FieldWork.CanChain(active!, tail, site) ? tail : null;
            // A chained job ends back at the truck's own yard.
            var home = after is not null ? after.Depot : depot?.Location;
            GeoPoint? start = after?.Site ?? depot?.Location;
            // Going on in a truck that's already out: the job is as tight as the most worked person in it.
            var shift = after?.Riders is { } on && hours is not null
                ? Shifts.Combine([hours.Of(c, m), .. on.Select(r => crews.FirstOrDefault(x => x.Id == r.CrewId) is { } rc
                    && rc.Members.FirstOrDefault(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) is { } rm ? hours.Of(rc, rm) : ShiftState.Fresh)])
                : hours?.Of(c, m);
            var people = after?.Riders?.Count ?? (m is null ? Math.Max(1, c.Members.Count) : 1);
            var job = hours is not null && start is { } from && home is { } h ? Shifts.Estimate(from, site, h, hours.Severity, after, people) : (TimeSpan?)null;

            // Leaving the yard: a free truck, and a crewmate to ride along when there's a seat.
            Vehicle? vehicle = null;
            CrewAssignee? partner = null;
            var noVehicle = false;
            DateTimeOffset? backAt = null;
            if (vehicles is not null && busy is null && depot is not null)
            {
                vehicle = m is null ? Fleet.RoomiestAt(vehicles, depot.Id, active ?? []) : Fleet.FreeAt(vehicles, depot.Id, active ?? []);
                noVehicle = vehicle is null;
                if (noVehicle) backAt = Fleet.NextBackAt(vehicles, depot.Id, active ?? []);
                if (m is not null && vehicle is { Seats: >= 2 })
                    partner = onDuty
                        .SelectMany(pc => pc.Members.Where(x => x != m && pc.DepotOf(x) == depot.Id).Select(x => new CrewAssignee(pc, x)))
                        .Where(x => active is null || FieldWork.BusyUntil(active, x.Crew.Id, x.Member!.Name) is null)
                        .Select(x => (x, s: hours?.Of(x.Crew, x.Member)))
                        .Where(t => t.s is null || (t.s.RestingUntil is null && (job is null || t.s.Left >= job)))
                        .OrderBy(t => t.x.Crew.Id != c.Id).ThenByDescending(t => t.s?.Left ?? TimeSpan.MaxValue).ThenBy(t => t.x.Member!.Name, StringComparer.Ordinal)
                        .Select(t => t.x).FirstOrDefault();
            }
            if (partner is not null && hours is not null && depot is not null)
                job = Shifts.Estimate(depot.Location, site, depot.Location, hours.Severity, null, 2); // two pairs of hands
            return new(c, m, depot, start is { } p ? Math.Round(Geo.DistanceMeters(p, site) / 1000, 1) : null, after is null ? busy : null, after, shift, job,
                vehicle, partner, noVehicle, backAt);
        }
        static IEnumerable<AssignCandidate> Nearest(IEnumerable<AssignCandidate> xs) =>
            xs.OrderBy(x => !x.Available).ThenBy(x => x.DistanceKm ?? double.MaxValue).ThenBy(x => x.Crew.Name).ThenBy(x => x.Member?.Name);

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
