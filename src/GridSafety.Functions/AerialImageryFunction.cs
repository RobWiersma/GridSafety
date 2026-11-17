using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using AWS.Lambda.Powertools.Logging;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>
/// Fetches one structure's aerial photo (public-domain NAIP via USGS) and caches it in the table, for the finding
/// captures. Two triggers, both on demand: a FindingCreated event (so the photo is ready by the time anyone opens
/// the finding), and the API, the first time someone opens an older finding. An export takes 15–30 s, too long to
/// do inside an API call. Each structure is fetched once; nothing is scheduled.
/// </summary>
public sealed class AerialImageryFunction(IGridRepository repo, IAerialSource source, IAerialStore store, TimeProvider clock)
{
    private static readonly HttpClient Http = CreateClient();

    public AerialImageryFunction() : this(
        Repo.FromEnvironment(), new UsgsNaipSource(Http),
        new DynamoAerialStore(new AmazonDynamoDBClient(), Env.Required("TABLE_NAME")), TimeProvider.System)
    { }

    [Logging(LogEvent = true)]
    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var cts = new CancellationTokenSource(context.RemainingTime - TimeSpan.FromSeconds(5));
        var status = await ProcessAsync(await JsonDocument.ParseAsync(input), cts.Token);
        Logger.LogInformation("Aerial photo: {Status}", status);
    }

    /// <summary>An <see cref="AerialRequest"/> from the API (already claimed), or a FindingCreated event from the bus.</summary>
    public async Task<string> ProcessAsync(JsonDocument doc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(doc);
        AerialRequest req;
        if (doc.RootElement.TryGetProperty("detail", out var detail))
        {
            var e = detail.Deserialize<GridEvent>(Json.Options) ?? throw new InvalidOperationException("Empty event detail");
            if (await repo.GetFindingAsync(e.EntityId, ct) is not { } f) return $"finding {e.EntityId} not found";
            if (!await store.TryClaimAsync(f.StructureId, clock.GetUtcNow(), ct)) return $"{f.StructureId} already cached or in progress";
            req = new AerialRequest(f.StructureId, f.Location);
        }
        else
        {
            req = doc.Deserialize<AerialRequest>(Json.Options) ?? throw new InvalidOperationException("Empty aerial request");
        }
        return $"{req.StructureId}: {await AerialPolicy.FetchAsync(req, source, store, clock, ct)}";
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(80) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GridSafety-demo/1.0");
        return http;
    }
}
