using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GridSafety.Core;

/// <summary>
/// How simulated flights (demo flights and "Simulate flight") behave. Supervisors edit it on the Settings page;
/// the API reads it when it launches a flight and passes the values along, so the flight itself needs no access to it.
/// </summary>
public sealed class SimulationSettings
{
    public const int MaxFindingsPerFlight = 25;

    public long Version { get; set; }

    /// <summary>Exactly this many findings per flight (capped by the structures on the line), or null for random (about 1 in 5 structures).</summary>
    public int? FindingsPerFlight { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public static SimulationSettings Default() => new();

    public static Dictionary<string, string[]> Validate(SimulationSettings s)
    {
        var e = new Dictionary<string, string[]>();
        if (s.FindingsPerFlight is < 0 or > MaxFindingsPerFlight)
            e["findingsPerFlight"] = [$"Findings per flight must be 0 to {MaxFindingsPerFlight}, or random."];
        return e;
    }
}

public interface ISimulationSettingsStore
{
    /// <summary>The current settings, or the defaults (Version 0) if nobody has edited them yet.</summary>
    Task<SimulationSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Saves if nobody else saved since <see cref="SimulationSettings.Version"/> was read; otherwise throws <see cref="ConcurrencyException"/>.</summary>
    Task SaveAsync(SimulationSettings settings, CancellationToken ct = default);
}

public sealed class InMemorySimulationSettingsStore : ISimulationSettingsStore
{
    private readonly Lock _gate = new();
    private string? _json;

    public Task<SimulationSettings> GetAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_json is null ? SimulationSettings.Default() : JsonSerializer.Deserialize<SimulationSettings>(_json, Json.Options)!);
    }

    public Task SaveAsync(SimulationSettings settings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = _json is null ? 0 : JsonSerializer.Deserialize<SimulationSettings>(_json, Json.Options)!.Version;
            if (current != settings.Version) throw new ConcurrencyException("simulation settings");
            settings.Version++;
            _json = JsonSerializer.Serialize(settings, Json.Options);
        }
        return Task.CompletedTask;
    }
}

/// <summary>PK=SETTINGS, SK=SIMULATION. Not SK=META, so edits stay out of the domain-event stream.</summary>
public sealed class DynamoSimulationSettingsStore(IAmazonDynamoDB db, string tableName) : ISimulationSettingsStore
{
    private static Dictionary<string, AttributeValue> Key() => new() { ["PK"] = new() { S = "SETTINGS" }, ["SK"] = new() { S = "SIMULATION" } };

    public async Task<SimulationSettings> GetAsync(CancellationToken ct = default)
    {
        var res = await db.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Key(), ConsistentRead = true }, ct);
        return res.Item is { Count: > 0 } item ? JsonSerializer.Deserialize<SimulationSettings>(item["Doc"].S, Json.Options)! : SimulationSettings.Default();
    }

    public async Task SaveAsync(SimulationSettings settings, CancellationToken ct = default)
    {
        var expected = settings.Version;
        settings.Version = expected + 1;
        var item = Key();
        item["EntityType"] = new() { S = "SimulationSettings" };
        item["Version"] = new() { N = settings.Version.ToString(CultureInfo.InvariantCulture) };
        item["Doc"] = new() { S = JsonSerializer.Serialize(settings, Json.Options) };
        var req = new PutItemRequest { TableName = tableName, Item = item };
        if (expected == 0) req.ConditionExpression = "attribute_not_exists(PK)";
        else
        {
            req.ConditionExpression = "Version = :v";
            req.ExpressionAttributeValues = new() { [":v"] = new() { N = expected.ToString(CultureInfo.InvariantCulture) } };
        }
        try { await db.PutItemAsync(req, ct); }
        catch (ConditionalCheckFailedException)
        {
            settings.Version = expected;
            throw new ConcurrencyException("simulation settings");
        }
    }
}
