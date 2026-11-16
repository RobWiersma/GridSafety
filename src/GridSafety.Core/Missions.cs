namespace GridSafety.Core;

public enum MissionStatus { Planned, InFlight, Completed, Aborted }

/// <summary>One drone flight inspecting one circuit.</summary>
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
    public required string CircuitId { get; init; }
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
    public List<StatusChange<MissionStatus>> History { get; init; } = [];
    public long Version { get; set; }

    public static Mission Plan(Circuit circuit, string droneId, string pilot, string? createdBy, DateTimeOffset now) => new()
    {
        Id = Ids.New(),
        CircuitId = circuit.Id,
        DroneId = droneId.Trim(),
        Pilot = pilot.Trim(),
        CreatedBy = createdBy,
        CreatedAt = now,
        UpdatedAt = now,
    };

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

    /// <summary>Share of the circuit's structures the drone has passed within inspection range of.</summary>
    public double Coverage(Circuit circuit) =>
        circuit.Structures.Count == 0 ? 0 : Math.Round((double)StructuresInspected.Count / circuit.Structures.Count, 3);
}
