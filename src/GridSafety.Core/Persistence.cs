using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GridSafety.Core;

public interface IGridRepository
{
    Task<Mission?> GetMissionAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Mission>> ListMissionsAsync(int limit = 100, CancellationToken ct = default);
    Task SaveMissionAsync(Mission mission, CancellationToken ct = default);

    Task<Finding?> GetFindingAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Finding>> ListFindingsAsync(int limit = 500, CancellationToken ct = default);
    Task SaveFindingAsync(Finding finding, CancellationToken ct = default);

    Task AppendTelemetryAsync(string missionId, IReadOnlyList<TelemetryPoint> points, CancellationToken ct = default);
    Task<IReadOnlyList<TelemetryPoint>> GetTrackAsync(string missionId, CancellationToken ct = default);

    Task<DroneState?> GetDroneAsync(string droneId, CancellationToken ct = default);
    Task<IReadOnlyList<DroneState>> ListDronesAsync(CancellationToken ct = default);
    Task SaveDroneAsync(DroneState drone, CancellationToken ct = default);
}

/// <summary>
/// Single-table design. Aggregates are stored as a JSON "Doc" plus the few attributes we key,
/// index or filter on.
///
///   Entity      PK                SK              GSI1PK     GSI1SK
///   Mission     MISSION#{id}      META            MISSION    {createdAt}
///   Telemetry   MISSION#{id}      TLM#{ts}        -          -          (TTL: ExpiresAt)
///   Finding     FINDING#{id}      META            FINDING    {createdAt}
///   Drone       DRONE#{id}        STATE           DRONE      {droneId}
///
/// One Query on PK=MISSION#{id} with SK begins_with TLM# returns a flight track in order.
/// The stream is filtered to SK=META, so only mission/finding changes become domain events.
/// </summary>
public sealed class DynamoGridRepository(IAmazonDynamoDB db, string tableName) : IGridRepository
{
    public static readonly TimeSpan TelemetryRetention = TimeSpan.FromDays(30);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<Mission?> GetMissionAsync(string id, CancellationToken ct = default) =>
        await GetDocAsync<Mission>($"MISSION#{id}", "META", ct);

    public async Task<IReadOnlyList<Mission>> ListMissionsAsync(int limit = 100, CancellationToken ct = default) =>
        await QueryIndexAsync<Mission>("MISSION", limit, ct);

    public Task SaveMissionAsync(Mission m, CancellationToken ct = default) =>
        PutVersionedAsync(m.Id, m.Version, v => m.Version = v, () => new()
        {
            ["PK"] = S($"MISSION#{m.Id}"),
            ["SK"] = S("META"),
            ["EntityType"] = S("Mission"),
            ["GSI1PK"] = S("MISSION"),
            ["GSI1SK"] = S(m.CreatedAt.ToString("O", Inv)),
            ["Status"] = S(m.Status.ToString()),
            ["Doc"] = S(JsonSerializer.Serialize(m, Json.Options)),
        }, ct);

    public async Task<Finding?> GetFindingAsync(string id, CancellationToken ct = default) =>
        await GetDocAsync<Finding>($"FINDING#{id}", "META", ct);

    public async Task<IReadOnlyList<Finding>> ListFindingsAsync(int limit = 500, CancellationToken ct = default) =>
        await QueryIndexAsync<Finding>("FINDING", limit, ct);

    public Task SaveFindingAsync(Finding f, CancellationToken ct = default) =>
        PutVersionedAsync(f.Id, f.Version, v => f.Version = v, () => new()
        {
            ["PK"] = S($"FINDING#{f.Id}"),
            ["SK"] = S("META"),
            ["EntityType"] = S("Finding"),
            ["GSI1PK"] = S("FINDING"),
            ["GSI1SK"] = S(f.CreatedAt.ToString("O", Inv)),
            ["Status"] = S(f.Status.ToString()),
            ["Severity"] = new AttributeValue { N = f.Severity.ToString(Inv) },
            ["Doc"] = S(JsonSerializer.Serialize(f, Json.Options)),
        }, ct);

    public async Task AppendTelemetryAsync(string missionId, IReadOnlyList<TelemetryPoint> points, CancellationToken ct = default)
    {
        foreach (var chunk in points.Chunk(25))
        {
            var writes = chunk.Select(p => new WriteRequest(new PutRequest(new Dictionary<string, AttributeValue>
            {
                ["PK"] = S($"MISSION#{missionId}"),
                ["SK"] = S($"TLM#{p.Timestamp.UtcDateTime.ToString("O", Inv)}"),
                ["EntityType"] = S("Telemetry"),
                ["Doc"] = S(JsonSerializer.Serialize(p, Json.Options)),
                ["ExpiresAt"] = new AttributeValue { N = (p.Timestamp + TelemetryRetention).ToUnixTimeSeconds().ToString(Inv) },
            }))).ToList();

            var pending = new Dictionary<string, List<WriteRequest>> { [tableName] = writes };
            for (var attempt = 0; pending.Count > 0 && pending.TryGetValue(tableName, out var left) && left.Count > 0; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt)), ct);
                var res = await db.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending }, ct);
                pending = res.UnprocessedItems ?? [];
                if (attempt > 6) throw new InvalidOperationException("DynamoDB kept throttling telemetry writes.");
            }
        }
    }

    public async Task<IReadOnlyList<TelemetryPoint>> GetTrackAsync(string missionId, CancellationToken ct = default)
    {
        var points = new List<TelemetryPoint>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var res = await db.QueryAsync(new QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "PK = :pk AND begins_with(SK, :tlm)",
                ExpressionAttributeValues = new() { [":pk"] = S($"MISSION#{missionId}"), [":tlm"] = S("TLM#") },
                ProjectionExpression = "Doc",
                ExclusiveStartKey = start,
            }, ct);
            points.AddRange((res.Items ?? []).Select(i => JsonSerializer.Deserialize<TelemetryPoint>(i["Doc"].S, Json.Options)!));
            start = res.LastEvaluatedKey is { Count: > 0 } ? res.LastEvaluatedKey : null;
        } while (start is not null);
        return points;
    }

    public async Task<DroneState?> GetDroneAsync(string droneId, CancellationToken ct = default) =>
        await GetDocAsync<DroneState>($"DRONE#{droneId}", "STATE", ct);

    public async Task<IReadOnlyList<DroneState>> ListDronesAsync(CancellationToken ct = default) =>
        await QueryIndexAsync<DroneState>("DRONE", 200, ct);

    public async Task SaveDroneAsync(DroneState d, CancellationToken ct = default)
    {
        try
        {
            // Last-writer-wins, but never let an older (out-of-order) message overwrite a newer position.
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = new()
                {
                    ["PK"] = S($"DRONE#{d.DroneId}"),
                    ["SK"] = S("STATE"),
                    ["EntityType"] = S("Drone"),
                    ["GSI1PK"] = S("DRONE"),
                    ["GSI1SK"] = S(d.DroneId),
                    ["LastSeen"] = S(d.LastSeen.UtcDateTime.ToString("O", Inv)),
                    ["Doc"] = S(JsonSerializer.Serialize(d, Json.Options)),
                },
                ConditionExpression = "attribute_not_exists(LastSeen) OR LastSeen <= :ts",
                ExpressionAttributeValues = new() { [":ts"] = S(d.LastSeen.UtcDateTime.ToString("O", Inv)) },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // A newer position is already stored; dropping this one is correct.
        }
    }

    private async Task<T?> GetDocAsync<T>(string pk, string sk, CancellationToken ct) where T : class
    {
        var res = await db.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = new() { ["PK"] = S(pk), ["SK"] = S(sk) },
            ConsistentRead = true,
        }, ct);
        return res.Item is { Count: > 0 } item ? JsonSerializer.Deserialize<T>(item["Doc"].S, Json.Options) : null;
    }

    private async Task<IReadOnlyList<T>> QueryIndexAsync<T>(string gsiPk, int limit, CancellationToken ct)
    {
        var results = new List<T>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var res = await db.QueryAsync(new QueryRequest
            {
                TableName = tableName,
                IndexName = "GSI1",
                KeyConditionExpression = "GSI1PK = :pk",
                ExpressionAttributeValues = new() { [":pk"] = S(gsiPk) },
                ScanIndexForward = false, // newest first
                Limit = limit - results.Count,
                ExclusiveStartKey = start,
            }, ct);
            results.AddRange((res.Items ?? []).Select(i => JsonSerializer.Deserialize<T>(i["Doc"].S, Json.Options)!));
            start = res.LastEvaluatedKey is { Count: > 0 } ? res.LastEvaluatedKey : null;
        } while (start is not null && results.Count < limit);
        return results;
    }

    private async Task PutVersionedAsync(string id, long expected, Action<long> setVersion, Func<Dictionary<string, AttributeValue>> build, CancellationToken ct)
    {
        setVersion(expected + 1);
        var item = build();
        item["Version"] = new AttributeValue { N = (expected + 1).ToString(Inv) };
        var req = new PutItemRequest { TableName = tableName, Item = item };
        if (expected == 0)
        {
            req.ConditionExpression = "attribute_not_exists(PK)";
        }
        else
        {
            req.ConditionExpression = "Version = :v";
            req.ExpressionAttributeValues = new() { [":v"] = new AttributeValue { N = expected.ToString(Inv) } };
        }

        try
        {
            await db.PutItemAsync(req, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            setVersion(expected);
            throw new ConcurrencyException(id);
        }
    }

    private static AttributeValue S(string s) => new() { S = s };

    /// <summary>Creates the table for local development (DynamoDB Local). Production uses CDK.</summary>
    public static async Task EnsureTableAsync(IAmazonDynamoDB db, string tableName, CancellationToken ct = default)
    {
        try
        {
            await db.DescribeTableAsync(tableName, ct);
        }
        catch (ResourceNotFoundException)
        {
            await db.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions =
                [
                    new("PK", ScalarAttributeType.S), new("SK", ScalarAttributeType.S),
                    new("GSI1PK", ScalarAttributeType.S), new("GSI1SK", ScalarAttributeType.S),
                ],
                KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = "GSI1",
                        KeySchema = [new("GSI1PK", KeyType.HASH), new("GSI1SK", KeyType.RANGE)],
                        Projection = new Projection { ProjectionType = ProjectionType.ALL },
                    },
                ],
            }, ct);
        }
    }
}

/// <summary>Zero-dependency store for local dev and tests. Honors the same concurrency contract.</summary>
public sealed class InMemoryGridRepository : IGridRepository
{
    private readonly ConcurrentDictionary<string, string> _missions = new();
    private readonly ConcurrentDictionary<string, string> _findings = new();
    private readonly ConcurrentDictionary<string, string> _drones = new();
    private readonly ConcurrentDictionary<string, List<TelemetryPoint>> _tracks = new();

    public Task<Mission?> GetMissionAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<Mission>(_missions, id));

    public Task<IReadOnlyList<Mission>> ListMissionsAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Mission>>(All<Mission>(_missions).OrderByDescending(m => m.CreatedAt).Take(limit).ToList());

    public Task SaveMissionAsync(Mission m, CancellationToken ct = default) => SaveVersioned(_missions, m.Id, m.Version, v => m.Version = v, () => m);

    public Task<Finding?> GetFindingAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<Finding>(_findings, id));

    public Task<IReadOnlyList<Finding>> ListFindingsAsync(int limit = 500, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Finding>>(All<Finding>(_findings).OrderByDescending(f => f.CreatedAt).Take(limit).ToList());

    public Task SaveFindingAsync(Finding f, CancellationToken ct = default) => SaveVersioned(_findings, f.Id, f.Version, v => f.Version = v, () => f);

    public Task AppendTelemetryAsync(string missionId, IReadOnlyList<TelemetryPoint> points, CancellationToken ct = default)
    {
        var track = _tracks.GetOrAdd(missionId, _ => []);
        lock (track)
        {
            track.AddRange(points);
            track.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TelemetryPoint>> GetTrackAsync(string missionId, CancellationToken ct = default)
    {
        if (!_tracks.TryGetValue(missionId, out var track)) return Task.FromResult<IReadOnlyList<TelemetryPoint>>([]);
        lock (track) return Task.FromResult<IReadOnlyList<TelemetryPoint>>(track.ToList());
    }

    public Task<DroneState?> GetDroneAsync(string droneId, CancellationToken ct = default) => Task.FromResult(Get<DroneState>(_drones, droneId));

    public Task<IReadOnlyList<DroneState>> ListDronesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DroneState>>(All<DroneState>(_drones).OrderBy(d => d.DroneId).ToList());

    public Task SaveDroneAsync(DroneState d, CancellationToken ct = default)
    {
        _drones.AddOrUpdate(d.DroneId, _ => Ser(d), (_, existing) =>
            JsonSerializer.Deserialize<DroneState>(existing, Json.Options)!.LastSeen <= d.LastSeen ? Ser(d) : existing);
        return Task.CompletedTask;
    }

    private static Task SaveVersioned<T>(ConcurrentDictionary<string, string> store, string id, long expected, Action<long> setVersion, Func<T> entity)
    {
        setVersion(expected + 1);
        var json = Ser(entity());
        var ok = expected == 0
            ? store.TryAdd(id, json)
            : store.TryGetValue(id, out var current)
              && JsonDocument.Parse(current).RootElement.GetProperty("version").GetInt64() == expected
              && store.TryUpdate(id, json, current);
        if (!ok)
        {
            setVersion(expected);
            throw new ConcurrencyException(id);
        }
        return Task.CompletedTask;
    }

    private static T? Get<T>(ConcurrentDictionary<string, string> store, string id) where T : class =>
        store.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<T>(json, Json.Options) : null;

    private static IEnumerable<T> All<T>(ConcurrentDictionary<string, string> store) =>
        store.Values.Select(v => JsonSerializer.Deserialize<T>(v, Json.Options)!);

    private static string Ser<T>(T value) => JsonSerializer.Serialize(value, Json.Options);
}
