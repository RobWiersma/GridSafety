namespace GridSafety.Core;

public enum MissionStatus { Planned, InFlight, Completed, Aborted }

/// <summary>One drone flight inspecting one circuit, or one battery's stretch of it (<see cref="Leg"/>).</summary>
public sealed class Mission
{
    public static readonly Workflow<MissionStatus> Workflow = new(new Dictionary<MissionStatus, MissionStatus[]>
    {
        [MissionStatus.Planned] = [MissionStatus.InFlight, MissionStatus.Aborted],
        [MissionStatus.InFlight] = [MissionStatus.Completed, MissionStatus.Aborted],
        [MissionStatus.Completed] = [],
        [MissionStatus.Aborted] = [],
    });

    public const int MaxRecentAlerts = 25;

    public required string Id { get; init; }
    /// <summary>Human-friendly callsign, e.g. "Amber Kestrel 07". Demo flights always get one; older missions may not.</summary>
    public string? Name { get; init; }
    public required string CircuitId { get; init; }
    /// <summary>
    /// Which flight of the line this is (1-based, see <see cref="FlightLegs"/>): a line longer than one battery allows is
    /// inspected over several flights. Null on missions from before lines were split, which covered the whole line.
    /// </summary>
    public int? Leg { get; init; }
    /// <summary>How many flights the whole line takes.</summary>
    public int? Legs { get; init; }
    /// <summary>
    /// Set when this flight was booked with others in one outing: the same drone and crew fly them back to back, swapping
    /// the battery in between. Shared by every flight of the outing.
    /// </summary>
    public string? OutingId { get; init; }
    /// <summary>This flight's place in its outing (1-based).</summary>
    public int? OutingFlight { get; init; }
    /// <summary>How many flights the outing has.</summary>
    public int? OutingFlights { get; init; }
    public required string DroneId { get; init; }
    public required string Pilot { get; init; }
    public string? CreatedBy { get; init; }
    public MissionStatus Status { get; set; } = MissionStatus.Planned;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public GeoPoint? LastPosition { get; set; }
    public double? BatteryPct { get; set; }
    /// <summary>Latest control-link signal strength, in dBm.</summary>
    public double? RssiDbm { get; set; }
    public double DistanceFlownMeters { get; set; }
    public List<string> StructuresInspected { get; init; } = [];
    public int FindingCount { get; set; }
    public List<DroneAlert> RecentAlerts { get; init; } = [];
    public string? ReportKey { get; set; }
    /// <summary>When a simulated flight was launched for this mission (demo flights, or "Simulate flight" on a planned one).</summary>
    public DateTimeOffset? SimulatedAt { get; set; }
    public List<StatusChange<MissionStatus>> History { get; init; } = [];
    public long Version { get; set; }

    /// <param name="leg">Which flight of the line (see <see cref="FlightLegs"/>); the first when null.</param>
    public static Mission Plan(Circuit circuit, string droneId, string pilot, string? createdBy, DateTimeOffset now, string? name = null, int? leg = null,
        (string Id, int Flight, int Flights)? outing = null) => new()
    {
        OutingId = outing?.Id,
        OutingFlight = outing?.Flight,
        OutingFlights = outing?.Flights,
        Id = Ids.New(),
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        CircuitId = circuit.Id,
        Leg = circuit.Leg(leg ?? 1)?.Number ?? 1,
        Legs = circuit.Legs().Count,
        DroneId = droneId.Trim(),
        Pilot = pilot.Trim(),
        CreatedBy = createdBy,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// Books flights <paramref name="legs"/> of a line as one outing: one mission per flight, in order along the line, for
    /// the same drone and pilot (a single flight books as a plain mission). Callsigns come from <paramref name="name"/>.
    /// </summary>
    public static IReadOnlyList<Mission> PlanOuting(Circuit circuit, IEnumerable<int> legs, string droneId, string pilot, string? createdBy,
        DateTimeOffset now, Func<string?>? name = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        var order = legs.Distinct().Order().ToList();
        if (order.Count == 0) throw new ArgumentException("Pick at least one flight.", nameof(legs));
        var outing = order.Count > 1 ? Ids.New() : null;
        return [.. order.Select((leg, i) => Plan(circuit, droneId, pilot, createdBy, now, name?.Invoke(), leg,
            outing is null ? null : (outing, i + 1, order.Count)))];
    }

    /// <summary>The outing this flight belongs to, or the flight itself when it was booked on its own.</summary>
    public string OutingKey => OutingId ?? Id;

    public void MoveTo(MissionStatus next, DateTimeOffset now, string? by = null, string? note = null)
    {
        Workflow.EnsureCanMove(Status, next);
        History.Add(new(Status, next, now, by, note));
        Status = next;
        if (next == MissionStatus.InFlight) StartedAt ??= now;
        if (Workflow.IsTerminal(next)) EndedAt = now;
        UpdatedAt = now;
    }

    public void RecordAlert(DroneAlert alert)
    {
        RecentAlerts.Insert(0, alert);
        if (RecentAlerts.Count > MaxRecentAlerts) RecentAlerts.RemoveRange(MaxRecentAlerts, RecentAlerts.Count - MaxRecentAlerts);
    }

    /// <summary>
    /// Marks unacknowledged alerts as seen: the one raised at <paramref name="at"/> (optionally of <paramref name="kind"/>),
    /// or all of them when <paramref name="at"/> is null. Already-acknowledged alerts keep their original acknowledgement.
    /// Returns how many changed.
    /// </summary>
    public int AcknowledgeAlerts(DateTimeOffset? at, AlertKind? kind, string by, DateTimeOffset now)
    {
        var changed = 0;
        for (var i = 0; i < RecentAlerts.Count; i++)
        {
            var a = RecentAlerts[i];
            if (a.AcknowledgedAt is not null) continue;
            if (at is { } t && (a.At != t || (kind is { } k && a.Kind != k))) continue;
            RecentAlerts[i] = a with { AcknowledgedAt = now, AcknowledgedBy = by };
            changed++;
        }
        if (changed > 0) UpdatedAt = now;
        return changed;
    }

    /// <summary>Share of the circuit's structures the drone has passed within inspection range of.</summary>
    /// <summary>Share of this flight's structures inspected (of the whole line for missions with no flight number).</summary>
    public double Coverage(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        var planned = circuit.Stretch(Leg).Structures;
        return planned.Count == 0 ? 0 : Math.Round((double)planned.Count(s => StructuresInspected.Contains(s.Id)) / planned.Count, 3);
    }
}
