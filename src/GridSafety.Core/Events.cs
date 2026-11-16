namespace GridSafety.Core;

public static class EventNames
{
    public const string Source = "gridsafety";
    public const string MissionStatusChanged = "MissionStatusChanged";
    public const string FindingCreated = "FindingCreated";
    public const string FindingStatusChanged = "FindingStatusChanged";
    public const string DroneAlertRaised = "DroneAlertRaised";
}

/// <summary>
/// Integration event on the EventBridge bus. Deliberately flat so rules can match on any field,
/// e.g. <c>{"detail": {"severity": [{"numeric": [">=", 4]}]}}</c>.
/// </summary>
public sealed record GridEvent
{
    public required string DetailType { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public string? MissionId { get; init; }
    public string? CircuitId { get; init; }
    public string? DroneId { get; init; }
    public string? PreviousStatus { get; init; }
    public string? NewStatus { get; init; }
    public int? Severity { get; init; }
    public string? Level { get; init; }
    public required string Summary { get; init; }
    public DateTimeOffset? TriageDueAt { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public interface IEventPublisher
{
    Task PublishAsync(IReadOnlyList<GridEvent> events, CancellationToken ct = default);
}

/// <summary>Local dev: keep recent events in memory instead of sending them to EventBridge.</summary>
public sealed class InMemoryEventPublisher : IEventPublisher
{
    private readonly List<GridEvent> _events = [];
    public IReadOnlyList<GridEvent> Published { get { lock (_events) return _events.ToList(); } }

    public Task PublishAsync(IReadOnlyList<GridEvent> events, CancellationToken ct = default)
    {
        lock (_events) _events.AddRange(events);
        return Task.CompletedTask;
    }
}
