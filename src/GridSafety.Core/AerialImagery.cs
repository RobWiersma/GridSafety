using System.Collections.Concurrent;
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GridSafety.Core;

/// <summary>Fetch the aerial photo of one structure, framed around its (public, OpenStreetMap) location.</summary>
public sealed record AerialRequest(string StructureId, GeoPoint Location);

public enum AerialStatus { Missing, Pending, Ready, Failed }

public sealed record AerialImage(AerialStatus Status, byte[]? Jpeg = null, DateTimeOffset? At = null)
{
    public static readonly AerialImage Missing = new(AerialStatus.Missing);
}

/// <summary>The ground area one aerial frame covers, centered on the structure. 4 px per metre at 640×400.</summary>
public static class AerialFrame
{
    public const double WidthMeters = 160;
    public const double HeightMeters = 100;
    public const double PixelsPerMeter = FindingImage.Width / WidthMeters;

    /// <summary>West, south, east, north in WGS84 degrees.</summary>
    public static (double W, double S, double E, double N) BoundingBox(GeoPoint center)
    {
        var dLat = HeightMeters / 2 / 111_320;
        var dLon = WidthMeters / 2 / (111_320 * Math.Cos(center.Lat * Math.PI / 180));
        return (center.Lon - dLon, center.Lat - dLat, center.Lon + dLon, center.Lat + dLat);
    }
}

public interface IAerialSource
{
    /// <summary>A 640×400 JPEG of <see cref="AerialFrame"/> around <paramref name="center"/>, or null where there is no imagery.</summary>
    Task<byte[]?> FetchAsync(GeoPoint center, CancellationToken ct = default);
}

/// <summary>
/// USDA NAIP orthoimagery (about 60 cm per pixel) from the USGS National Map. It is public domain, needs no key,
/// and gets only the structure's public coordinates. An export takes 15–30 s, so frames are fetched once in the
/// background and cached (<see cref="IAerialStore"/>), never on a page view.
/// </summary>
public sealed class UsgsNaipSource(HttpClient http) : IAerialSource
{
    public const string Attribution = "Imagery: USDA NAIP via USGS The National Map (public domain)";
    private const string ExportUrl = "https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPImagery/ImageServer/exportImage";

    public static Uri Url(GeoPoint center)
    {
        var (w, s, e, n) = AerialFrame.BoundingBox(center);
        return new Uri(string.Create(CultureInfo.InvariantCulture,
            $"{ExportUrl}?bbox={w:F6},{s:F6},{e:F6},{n:F6}&bboxSR=4326&size={FindingImage.Width},{FindingImage.Height}&format=jpg&f=image"));
    }

    public async Task<byte[]?> FetchAsync(GeoPoint center, CancellationToken ct = default)
    {
        using var res = await http.GetAsync(Url(center), ct);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        // ArcGIS reports errors as a 200 with a JSON body; anything that isn't a JPEG means "no imagery here".
        return IsJpeg(bytes) ? bytes : null;
    }

    public static bool IsJpeg(byte[] b) => b.Length > 1000 && b[0] == 0xFF && b[1] == 0xD8;
}

/// <summary>
/// One cached frame per structure, plus a short-lived claim while it is being fetched (so polling doesn't start
/// a second fetch) and a failure marker (so a spot with no imagery isn't retried on every view).
/// </summary>
public interface IAerialStore
{
    Task<AerialImage> GetAsync(string structureId, CancellationToken ct = default);

    /// <summary>Marks the frame as being fetched; false if it is ready, or already pending or recently failed.</summary>
    Task<bool> TryClaimAsync(string structureId, DateTimeOffset now, CancellationToken ct = default);

    Task SaveAsync(string structureId, byte[] jpeg, DateTimeOffset now, CancellationToken ct = default);
    Task MarkFailedAsync(string structureId, DateTimeOffset now, CancellationToken ct = default);
}

public static class AerialPolicy
{
    /// <summary>A claim older than this is treated as a fetch that died (longer than the Lambda's 5-minute timeout).</summary>
    public static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(6);

    /// <summary>How long to wait before trying a failed spot again. USGS errors are usually brief.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(15);

    /// <summary>Pauses between attempts when USGS errors or times out (its export service sometimes returns 502/504).</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    /// <summary>What a reader should make of a stored record right now (stale claims and failures read as Missing).</summary>
    public static AerialStatus Effective(AerialImage img, DateTimeOffset now) => img.Status switch
    {
        AerialStatus.Pending when img.At is { } at && now - at > PendingFor => AerialStatus.Missing,
        AerialStatus.Failed when img.At is { } at && now - at > RetryFailedAfter => AerialStatus.Missing,
        var s => s,
    };

    /// <summary>
    /// Fetch and store one frame, retrying errors and timeouts (<see cref="RetryDelays"/>). Never throws for a bad fetch:
    /// it records a failure instead, and reports each error to <paramref name="onError"/> for the logs.
    /// </summary>
    public static async Task<AerialStatus> FetchAsync(
        AerialRequest req, IAerialSource source, IAerialStore store, TimeProvider clock, CancellationToken ct = default,
        Action<int, Exception>? onError = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        var delays = retryDelays ?? RetryDelays;
        byte[]? jpeg = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                jpeg = await source.FetchAsync(req.Location, ct);
                break; // a JPEG, or null for "no imagery here", which retrying won't change
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                onError?.Invoke(attempt + 1, ex);
                if (attempt >= delays.Count) break;
                await Task.Delay(delays[attempt], clock, ct);
            }
        }

        if (jpeg is null)
        {
            await store.MarkFailedAsync(req.StructureId, clock.GetUtcNow(), ct);
            return AerialStatus.Failed;
        }
        await store.SaveAsync(req.StructureId, jpeg, clock.GetUtcNow(), ct);
        return AerialStatus.Ready;
    }
}

public sealed class InMemoryAerialStore : IAerialStore
{
    private readonly ConcurrentDictionary<string, AerialImage> _items = new();

    public Task<AerialImage> GetAsync(string structureId, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(structureId, AerialImage.Missing));

    public Task<bool> TryClaimAsync(string structureId, DateTimeOffset now, CancellationToken ct = default)
    {
        while (true)
        {
            if (_items.TryGetValue(structureId, out var current))
            {
                if (AerialPolicy.Effective(current, now) != AerialStatus.Missing) return Task.FromResult(false);
                if (_items.TryUpdate(structureId, new(AerialStatus.Pending, At: now), current)) return Task.FromResult(true);
            }
            else if (_items.TryAdd(structureId, new(AerialStatus.Pending, At: now))) return Task.FromResult(true);
        }
    }

    public Task SaveAsync(string structureId, byte[] jpeg, DateTimeOffset now, CancellationToken ct = default)
    {
        _items[structureId] = new(AerialStatus.Ready, jpeg, now);
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(string structureId, DateTimeOffset now, CancellationToken ct = default)
    {
        _items[structureId] = new(AerialStatus.Failed, At: now);
        return Task.CompletedTask;
    }
}

/// <summary>
/// PK=AERIAL#{structureId}, SK=IMAGE (not META, so it stays out of the domain-event stream). The JPEG (~40 KB) is a
/// binary attribute; claims and failures carry a TTL so the table cleans them up.
/// </summary>
public sealed class DynamoAerialStore(IAmazonDynamoDB db, string tableName) : IAerialStore
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Dictionary<string, AttributeValue> Key(string structureId) =>
        new() { ["PK"] = new() { S = $"AERIAL#{structureId}" }, ["SK"] = new() { S = "IMAGE" } };

    public async Task<AerialImage> GetAsync(string structureId, CancellationToken ct = default)
    {
        var res = await db.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Key(structureId) }, ct);
        if (res.Item is not { Count: > 0 } item || !Enum.TryParse<AerialStatus>(item["Status"].S, out var status)) return AerialImage.Missing;
        var at = DateTimeOffset.Parse(item["At"].S, Inv);
        return new(status, item.TryGetValue("Jpeg", out var b) ? b.B.ToArray() : null, at);
    }

    public async Task<bool> TryClaimAsync(string structureId, DateTimeOffset now, CancellationToken ct = default)
    {
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = Marker(structureId, AerialStatus.Pending, now, AerialPolicy.PendingFor),
                // Free to claim: nothing stored, or a claim that died, or a failure that is old enough to retry.
                ConditionExpression = "attribute_not_exists(PK) OR (#s = :pending AND #at < :staleClaim) OR (#s = :failed AND #at < :retry)",
                ExpressionAttributeNames = new() { ["#s"] = "Status", ["#at"] = "At" },
                ExpressionAttributeValues = new()
                {
                    [":pending"] = new() { S = nameof(AerialStatus.Pending) },
                    [":failed"] = new() { S = nameof(AerialStatus.Failed) },
                    [":staleClaim"] = new() { S = Stamp(now - AerialPolicy.PendingFor) },
                    [":retry"] = new() { S = Stamp(now - AerialPolicy.RetryFailedAfter) },
                },
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task SaveAsync(string structureId, byte[] jpeg, DateTimeOffset now, CancellationToken ct = default)
    {
        var item = Key(structureId);
        item["EntityType"] = new() { S = "AerialImage" };
        item["Status"] = new() { S = nameof(AerialStatus.Ready) };
        item["At"] = new() { S = Stamp(now) };
        item["Jpeg"] = new() { B = new MemoryStream(jpeg) };
        await db.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
    }

    public async Task MarkFailedAsync(string structureId, DateTimeOffset now, CancellationToken ct = default) =>
        await db.PutItemAsync(new PutItemRequest { TableName = tableName, Item = Marker(structureId, AerialStatus.Failed, now, AerialPolicy.RetryFailedAfter) }, ct);

    private static Dictionary<string, AttributeValue> Marker(string structureId, AerialStatus status, DateTimeOffset now, TimeSpan keep)
    {
        var item = Key(structureId);
        item["EntityType"] = new() { S = "AerialImage" };
        item["Status"] = new() { S = status.ToString() };
        item["At"] = new() { S = Stamp(now) };
        item["ExpiresAt"] = new() { N = (now + keep + TimeSpan.FromDays(1)).ToUnixTimeSeconds().ToString(Inv) };
        return item;
    }

    // Fixed-width UTC timestamps compare correctly as strings.
    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv);
}
