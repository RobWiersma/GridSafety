namespace GridSafety.Core;

/// <summary>
/// Live push: when something changes, connected browsers get a tiny message naming what changed
/// (<c>{"topics":["drones","missions"]}</c>) and refetch those screens through the normal API. The message never carries
/// data, so sign-in, roles and the guest view's redaction all stay in the existing endpoints.
///
/// In AWS a second consumer of the table's change stream sends these through an API Gateway WebSocket API; locally the
/// API notices its own writes and sends them over <c>/api/live</c>. Both use <see cref="ForKey"/>, so a topic always
/// means "a row of this kind was written".
/// </summary>
public static class LiveTopics
{
    public const string Drones = "drones";
    public const string Missions = "missions";
    public const string Findings = "findings";
    public const string Resources = "resources";

    /// <summary>Table rows whose changes are worth a push; everything else (telemetry points, visits, idempotency keys,
    /// the connections themselves) never is. Also the stream filter for the broadcaster.</summary>
    public static readonly IReadOnlyList<string> SortKeys = ["META", "STATE", "CATALOG"];

    /// <summary>The topic a write to this table key signals, or null when browsers don't need to hear about it.</summary>
    public static string? ForKey(string? pk, string? sk) => (pk, sk) switch
    {
        ({ } p, "META") when p.StartsWith("MISSION#", StringComparison.Ordinal) => Missions,
        ({ } p, "META") when p.StartsWith("FINDING#", StringComparison.Ordinal) => Findings,
        ({ } p, "STATE") when p.StartsWith("DRONE#", StringComparison.Ordinal) => Drones,
        ("RESOURCES", "CATALOG") => Resources,
        _ => null,
    };

    /// <summary>The push message for a set of topics (sorted, no duplicates).</summary>
    public static string Message(IEnumerable<string> topics) =>
        System.Text.Json.JsonSerializer.Serialize(new { topics = topics.Distinct().Order(StringComparer.Ordinal).ToArray() });
}

/// <summary>Something that tells connected browsers a topic changed.</summary>
public interface ILiveNotifier
{
    void Notify(string topic);
}

/// <summary>Repository that signals a topic after every successful write (local live push; AWS uses the table stream).</summary>
public sealed class LiveGridRepository(IGridRepository inner, ILiveNotifier live) : IGridRepository
{
    public Task<Mission?> GetMissionAsync(string id, CancellationToken ct = default) => inner.GetMissionAsync(id, ct);
    public Task<IReadOnlyList<Mission>> ListMissionsAsync(int limit = 100, CancellationToken ct = default) => inner.ListMissionsAsync(limit, ct);
    public async Task SaveMissionAsync(Mission mission, CancellationToken ct = default)
    {
        await inner.SaveMissionAsync(mission, ct);
        live.Notify(LiveTopics.Missions);
    }

    public Task<Finding?> GetFindingAsync(string id, CancellationToken ct = default) => inner.GetFindingAsync(id, ct);
    public Task<IReadOnlyList<Finding>> ListFindingsAsync(int limit = 500, CancellationToken ct = default) => inner.ListFindingsAsync(limit, ct);
    public async Task SaveFindingAsync(Finding finding, CancellationToken ct = default)
    {
        await inner.SaveFindingAsync(finding, ct);
        live.Notify(LiveTopics.Findings);
    }

    // Telemetry points are never pushed on their own: the drone state saved with them is.
    public Task AppendTelemetryAsync(string missionId, IReadOnlyList<TelemetryPoint> points, CancellationToken ct = default) =>
        inner.AppendTelemetryAsync(missionId, points, ct);
    public Task<IReadOnlyList<TelemetryPoint>> GetTrackAsync(string missionId, CancellationToken ct = default) => inner.GetTrackAsync(missionId, ct);

    public Task<DroneState?> GetDroneAsync(string droneId, CancellationToken ct = default) => inner.GetDroneAsync(droneId, ct);
    public Task<IReadOnlyList<DroneState>> ListDronesAsync(CancellationToken ct = default) => inner.ListDronesAsync(ct);
    public async Task SaveDroneAsync(DroneState drone, CancellationToken ct = default)
    {
        await inner.SaveDroneAsync(drone, ct);
        live.Notify(LiveTopics.Drones);
    }
}

/// <summary>Resource catalog store that signals <see cref="LiveTopics.Resources"/> after every save.</summary>
public sealed class LiveResourceStore(IResourceStore inner, ILiveNotifier live) : IResourceStore
{
    public Task<ResourceCatalog> GetAsync(CancellationToken ct = default) => inner.GetAsync(ct);
    public async Task SaveAsync(ResourceCatalog catalog, CancellationToken ct = default)
    {
        await inner.SaveAsync(catalog, ct);
        live.Notify(LiveTopics.Resources);
    }
}
