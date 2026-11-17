namespace GridSafety.Core;

public enum FindingType
{
    VegetationEncroachment,
    DamagedInsulator,
    CrossarmDamage,
    ThermalHotspot,
    MissingHardware,
    ConductorDamage,
}

public enum FindingStatus { New, Triaged, Assigned, Resolved, Dismissed }

/// <summary>An issue spotted during inspection. Doubles as a work order once triaged.</summary>
public sealed class Finding
{
    public static readonly Workflow<FindingStatus> Workflow = new(new Dictionary<FindingStatus, FindingStatus[]>
    {
        [FindingStatus.New] = [FindingStatus.Triaged, FindingStatus.Dismissed],
        [FindingStatus.Triaged] = [FindingStatus.Assigned, FindingStatus.Dismissed],
        [FindingStatus.Assigned] = [FindingStatus.Resolved],
        [FindingStatus.Resolved] = [],
        [FindingStatus.Dismissed] = [],
    });

    public required string Id { get; init; }
    public required string MissionId { get; init; }
    public required string CircuitId { get; init; }
    public required string StructureId { get; init; }
    public required string StructureLabel { get; init; }
    public required FindingType Type { get; init; }
    public required int Severity { get; init; }
    public double Confidence { get; init; }
    public GeoPoint Location { get; init; }
    public string? Description { get; init; }
    public FindingStatus Status { get; set; } = FindingStatus.New;
    public string? AssignedTo { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? TriageDueAt { get; init; }
    public bool Escalated { get; set; }
    /// <summary>The simulated truck roll sent when the work order was assigned, if the assignee had a depot.</summary>
    public FieldDispatch? Dispatch { get; set; }
    public List<StatusChange<FindingStatus>> History { get; init; } = [];
    public long Version { get; set; }

    public bool IsOverdue(DateTimeOffset now) => Status == FindingStatus.New && TriageDueAt is { } due && now > due;

    public void MoveTo(FindingStatus next, DateTimeOffset now, string? by, string? note = null, string? assignee = null)
    {
        Workflow.EnsureCanMove(Status, next);
        if (next == FindingStatus.Assigned)
        {
            if (string.IsNullOrWhiteSpace(assignee)) throw new ArgumentException("An assignee (crew or person) is required.", nameof(assignee));
            AssignedTo = assignee.Trim();
        }
        History.Add(new(Status, next, now, by, string.IsNullOrWhiteSpace(note) ? null : note.Trim()));
        Status = next;
        UpdatedAt = now;
    }

    /// <summary>Only supervisors may close a finding without a field fix.</summary>
    public static bool RequiresSupervisor(FindingStatus target) => target == FindingStatus.Dismissed;
}

/// <summary>Triage service levels. Higher severity means a tighter window before escalation.</summary>
public static class TriageSla
{
    public static TimeSpan? WindowFor(int severity) => severity switch
    {
        >= 5 => TimeSpan.FromHours(4),
        4 => TimeSpan.FromHours(24),
        3 => TimeSpan.FromHours(72),
        _ => null,
    };

    public static string Describe(int severity) => severity switch
    {
        >= 5 => "Critical: imminent fire or outage risk",
        4 => "High: schedule repair this week",
        3 => "Moderate: add to maintenance plan",
        2 => "Low: monitor",
        _ => "Informational",
    };
}
