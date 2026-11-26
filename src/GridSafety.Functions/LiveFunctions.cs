using System.Globalization;
using System.Text;
using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Tracing;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>
/// Open browser sockets on the WebSocket API, one row each: PK=LIVE, SK=CONN#{connectionId}. The SK is not one of
/// <see cref="LiveTopics.SortKeys"/>, so connecting never triggers a push, and the TTL clears rows whose disconnect
/// was missed (API Gateway closes every socket after two hours anyway).
/// </summary>
public sealed class LiveConnections(IAmazonDynamoDB db, string tableName)
{
    public const string Pk = "LIVE";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    /// <summary>The broadcaster only reads this many; a demo never gets near it, a flood of sockets can't fan out further.</summary>
    public const int MaxConnections = 500;

    public Task AddAsync(string connectionId, DateTimeOffset now, CancellationToken ct = default) =>
        db.PutItemAsync(new PutItemRequest
        {
            TableName = tableName,
            Item = new()
            {
                ["PK"] = new AttributeValue { S = Pk },
                ["SK"] = new AttributeValue { S = $"CONN#{connectionId}" },
                ["EntityType"] = new AttributeValue { S = "LiveConnection" },
                ["ExpiresAt"] = new AttributeValue { N = now.Add(Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) },
            },
        }, ct);

    public Task RemoveAsync(string connectionId, CancellationToken ct = default) =>
        db.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = tableName,
            Key = new() { ["PK"] = new AttributeValue { S = Pk }, ["SK"] = new AttributeValue { S = $"CONN#{connectionId}" } },
        }, ct);

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var res = await db.QueryAsync(new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :c)",
            ExpressionAttributeValues = new() { [":pk"] = new AttributeValue { S = Pk }, [":c"] = new AttributeValue { S = "CONN#" } },
            ProjectionExpression = "SK",
            Limit = MaxConnections,
        }, ct);
        return (res.Items ?? []).Select(i => i["SK"].S["CONN#".Length..]).ToList();
    }
}

/// <summary>$connect and $disconnect on the WebSocket API. Anyone may listen: messages only name topics, never data.</summary>
public sealed class LiveConnectionFunction(LiveConnections connections, TimeProvider clock)
{
    public LiveConnectionFunction() : this(new LiveConnections(new AmazonDynamoDBClient(), Env.Required("TABLE_NAME")), TimeProvider.System) { }

    [Logging(LogEvent = false)]
    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task<APIGatewayProxyResponse> HandleAsync(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var id = request.RequestContext.ConnectionId;
        switch (request.RequestContext.EventType)
        {
            case "CONNECT": await connections.AddAsync(id, clock.GetUtcNow()); break;
            case "DISCONNECT": await connections.RemoveAsync(id); break;
        }
        return new APIGatewayProxyResponse { StatusCode = 200 };
    }
}

/// <summary>
/// Second consumer of the table's change stream (the StreamPublisher is the first): turns a batch of writes into topics
/// and sends one small message to every open socket. Best effort: a browser that misses one still catches up on its
/// slow fallback poll, so failures are logged and never retried.
/// </summary>
public sealed class LiveBroadcaster(LiveConnections connections, IAmazonApiGatewayManagementApi sockets)
{
    public LiveBroadcaster() : this(
        new LiveConnections(new AmazonDynamoDBClient(), Env.Required("TABLE_NAME")),
        new AmazonApiGatewayManagementApiClient(new AmazonApiGatewayManagementApiConfig { ServiceURL = Env.Required("LIVE_ENDPOINT") }))
    { }

    [Logging(LogEvent = false)]
    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task HandleAsync(DynamoDBEvent evt, ILambdaContext context)
    {
        var topics = Topics(evt.Records.Select(r => (Key(r, "PK"), Key(r, "SK"))));
        if (topics.Count == 0) return;
        Tracing.AddAnnotation("topics", string.Join(",", topics));

        var ids = await connections.ListAsync();
        var payload = Encoding.UTF8.GetBytes(LiveTopics.Message(topics));
        int sent = 0, gone = 0;
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (id, ct) =>
        {
            try
            {
                await sockets.PostToConnectionAsync(new PostToConnectionRequest { ConnectionId = id, Data = new MemoryStream(payload) }, ct);
                Interlocked.Increment(ref sent);
            }
            catch (GoneException)
            {
                Interlocked.Increment(ref gone);
                await connections.RemoveAsync(id, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Push to {Connection} failed: {Reason}", id, ex.Message);
            }
        });
        Logger.LogInformation("Pushed {Topics} to {Sent} socket(s), {Gone} gone", string.Join(",", topics), sent, gone);
    }

    /// <summary>The distinct topics a batch of written keys signals.</summary>
    public static IReadOnlyList<string> Topics(IEnumerable<(string? Pk, string? Sk)> keys) =>
        keys.Select(k => LiveTopics.ForKey(k.Pk, k.Sk)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

    private static string? Key(DynamoDBEvent.DynamodbStreamRecord r, string name) =>
        r.Dynamodb?.Keys is { } keys && keys.TryGetValue(name, out var v) ? v.S : null;
}
