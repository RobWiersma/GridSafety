using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GridSafety.Core;

/// <summary>
/// One page view, already reduced to coarse, non-identifying facts. The raw IP address and user agent never
/// leave <see cref="VisitAnonymizer"/>: they are hashed with a salt that exists for one UTC day and is then
/// deleted, so a visitor can be counted once per day but never recognized across days or traced back.
/// </summary>
public sealed record PageVisit(
    DateOnly Day, string VisitorKey, string Page, string Referrer, string Device, string Browser, string Os,
    string Country, string Language, bool SignedIn);

/// <summary>
/// Visitor totals for one UTC day. Breakdowns count unique visitors, except <see cref="Pages"/>, which counts views.
/// Signed-in views (the team's own) are only in <see cref="SignedInViews"/>, never in the visitor numbers.
/// </summary>
public sealed record DailyVisits(
    DateOnly Day, int PageViews, int UniqueVisitors, int SignedInViews,
    IReadOnlyDictionary<string, int> Pages, IReadOnlyDictionary<string, int> Referrers, IReadOnlyDictionary<string, int> Devices,
    IReadOnlyDictionary<string, int> Browsers, IReadOnlyDictionary<string, int> OperatingSystems,
    IReadOnlyDictionary<string, int> Countries, IReadOnlyDictionary<string, int> Languages);

public interface IVisitStore
{
    /// <summary>A random salt for the day, created on first use and deleted after the day ends.</summary>
    Task<string> GetDailySaltAsync(DateOnly day, CancellationToken ct = default);
    Task RecordAsync(PageVisit visit, CancellationToken ct = default);
    Task<IReadOnlyList<DailyVisits>> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Turns request details into a <see cref="PageVisit"/>. Nothing it returns identifies a person.</summary>
public static partial class VisitAnonymizer
{
    public static readonly string[] KnownPages = ["ops", "findings", "missions", "mission", "visitors"];

    public static bool IsBot(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) || BotPattern().IsMatch(userAgent);

    /// <summary>Daily visitor key: SHA-256 of the day's salt, IP and user agent, truncated. Unlinkable once the salt is gone.</summary>
    public static string VisitorKey(string salt, string? ip, string? userAgent)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}|{ip}|{userAgent}"));
        return Convert.ToHexStringLower(bytes, 0, 12);
    }

    public static PageVisit Describe(DateOnly day, string visitorKey, string? page, string? referrer, string? ownHost,
        string? userAgent, string? country, string? acceptLanguage, bool signedIn) =>
        new(day, visitorKey,
            Page: page is not null && KnownPages.Contains(page) ? page : "other",
            Referrer: ReferrerHost(referrer, ownHost),
            Device: Device(userAgent),
            Browser: Browser(userAgent),
            Os: OperatingSystem(userAgent),
            Country: country is { Length: 2 } c && c.All(char.IsAsciiLetter) ? c.ToUpperInvariant() : "unknown",
            Language: Language(acceptLanguage),
            SignedIn: signedIn);

    /// <summary>Just the referring site's host name ("news.ycombinator.com"), or "direct". Paths and queries are dropped.</summary>
    public static string ReferrerHost(string? referrer, string? ownHost)
    {
        if (string.IsNullOrWhiteSpace(referrer)) return "direct";
        if (!Uri.TryCreate(referrer.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "other";
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (ownHost is not null && host.Equals(ownHost.Split(':')[0], StringComparison.OrdinalIgnoreCase)) return "direct";
        return host.Length <= 64 && HostPattern().IsMatch(host) ? host : "other";
    }

    public static string Device(string? ua) => ua switch
    {
        null => "unknown",
        _ when ua.Contains("iPad", StringComparison.Ordinal) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) => "Tablet",
        _ when ua.Contains("Mobi", StringComparison.Ordinal) || ua.Contains("Android", StringComparison.Ordinal) => "Mobile",
        _ => "Desktop",
    };

    public static string Browser(string? ua) => ua switch
    {
        null => "unknown",
        _ when ua.Contains("Edg/", StringComparison.Ordinal) => "Edge",
        _ when ua.Contains("OPR/", StringComparison.Ordinal) => "Opera",
        _ when ua.Contains("SamsungBrowser", StringComparison.Ordinal) => "Samsung Internet",
        _ when ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS", StringComparison.Ordinal) => "Firefox",
        _ when ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS", StringComparison.Ordinal) => "Chrome",
        _ when ua.Contains("Safari/", StringComparison.Ordinal) => "Safari",
        _ => "Other",
    };

    public static string OperatingSystem(string? ua) => ua switch
    {
        null => "unknown",
        _ when ua.Contains("Windows", StringComparison.Ordinal) => "Windows",
        _ when ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPad", StringComparison.Ordinal) => "iOS",
        _ when ua.Contains("Android", StringComparison.Ordinal) => "Android",
        _ when ua.Contains("Mac OS X", StringComparison.Ordinal) => "macOS",
        _ when ua.Contains("CrOS", StringComparison.Ordinal) => "ChromeOS",
        _ when ua.Contains("Linux", StringComparison.Ordinal) => "Linux",
        _ => "Other",
    };

    /// <summary>Primary language only ("en" from "en-US,en;q=0.9").</summary>
    public static string Language(string? acceptLanguage)
    {
        var first = acceptLanguage?.Split(',')[0].Split(';')[0].Split('-')[0].Trim().ToLowerInvariant();
        return first is { Length: 2 or 3 } l && l.All(char.IsAsciiLetterLower) ? l : "unknown";
    }

    [GeneratedRegex(@"bot|crawl|spider|slurp|preview|headless|lighthouse|curl|wget|python|httpclient|monitor", RegexOptions.IgnoreCase)]
    private static partial Regex BotPattern();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")]
    private static partial Regex HostPattern();
}

/// <summary>Builds <see cref="DailyVisits"/> from counters named "{dimension}#{value}".</summary>
internal static class VisitCounters
{
    public const string PageViews = "views";
    public const string Uniques = "uniques";
    public const string SignedInViews = "signedviews";

    /// <summary>
    /// Counters to bump for a view. Per-visitor ones count only that visitor's first view of the day.
    /// A signed-in view is the team's own: it bumps only its own total and none of the visitor counters.
    /// </summary>
    public static IEnumerable<string> For(PageVisit v, bool firstViewToday)
    {
        if (v.SignedIn) { yield return SignedInViews; yield break; }
        yield return PageViews;
        yield return $"page#{v.Page}";
        if (!firstViewToday) yield break;
        yield return Uniques;
        yield return $"ref#{v.Referrer}";
        yield return $"device#{v.Device}";
        yield return $"browser#{v.Browser}";
        yield return $"os#{v.Os}";
        yield return $"country#{v.Country}";
        yield return $"lang#{v.Language}";
    }

    public static DailyVisits ToDay(DateOnly day, IReadOnlyDictionary<string, int> counters)
    {
        int Get(string k) => counters.GetValueOrDefault(k);
        IReadOnlyDictionary<string, int> Dim(string prefix) => counters
            .Where(kv => kv.Key.StartsWith(prefix + "#", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[(prefix.Length + 1)..], kv => kv.Value);
        return new(day, Get(PageViews), Get(Uniques), Get(SignedInViews),
            Dim("page"), Dim("ref"), Dim("device"), Dim("browser"), Dim("os"), Dim("country"), Dim("lang"));
    }
}

public sealed class InMemoryVisitStore : IVisitStore
{
    private readonly ConcurrentDictionary<DateOnly, string> _salts = new();
    private readonly ConcurrentDictionary<(DateOnly, string), byte> _seen = new();
    private readonly ConcurrentDictionary<(DateOnly, string), int> _counters = new();

    public Task<string> GetDailySaltAsync(DateOnly day, CancellationToken ct = default) =>
        Task.FromResult(_salts.GetOrAdd(day, _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    public Task RecordAsync(PageVisit visit, CancellationToken ct = default)
    {
        var first = !visit.SignedIn && _seen.TryAdd((visit.Day, visit.VisitorKey), 0);
        foreach (var c in VisitCounters.For(visit, first)) _counters.AddOrUpdate((visit.Day, c), 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DailyVisits>> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var days = new List<DailyVisits>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var day = d;
            days.Add(VisitCounters.ToDay(day, _counters.Where(kv => kv.Key.Item1 == day).ToDictionary(kv => kv.Key.Item2, kv => kv.Value)));
        }
        return Task.FromResult<IReadOnlyList<DailyVisits>>(days);
    }
}

/// <summary>
/// Visits in the main table, outside the domain-event stream (which only carries SK=META):
///
///   Item            PK                 SK              TTL (ExpiresAt)
///   Daily salt      VISITSALT          {day}           2 days
///   Visitor marker  VISITS#{day}       V#{key}, S#{key} (signed in)   2 days
///   Counter         VISITS#{day}       C#{name}        400 days    attribute Hits = count
/// </summary>
public sealed class DynamoVisitStore(IAmazonDynamoDB db, string tableName) : IVisitStore
{
    private static readonly TimeSpan ShortLived = TimeSpan.FromDays(2);
    private static readonly TimeSpan CounterRetention = TimeSpan.FromDays(400);

    public async Task<string> GetDailySaltAsync(DateOnly day, CancellationToken ct = default)
    {
        var key = new Dictionary<string, AttributeValue> { ["PK"] = S("VISITSALT"), ["SK"] = S(Day(day)) };
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = new(key) { ["Salt"] = S(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))), ["ExpiresAt"] = Expiry(day, ShortLived) },
                ConditionExpression = "attribute_not_exists(PK)",
            }, ct);
        }
        catch (ConditionalCheckFailedException) { /* another request created today's salt first */ }
        var res = await db.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true }, ct);
        return res.Item["Salt"].S;
    }

    public async Task RecordAsync(PageVisit visit, CancellationToken ct = default)
    {
        var first = !visit.SignedIn && await MarkAsync(visit, "V#", ct);

        await Task.WhenAll(VisitCounters.For(visit, first).Select(c => db.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = new() { ["PK"] = S($"VISITS#{Day(visit.Day)}"), ["SK"] = S($"C#{c}") },
            UpdateExpression = "ADD #hits :one SET ExpiresAt = :exp",
            ExpressionAttributeNames = new() { ["#hits"] = "Hits" },
            ExpressionAttributeValues = new() { [":one"] = new AttributeValue { N = "1" }, [":exp"] = Expiry(visit.Day, CounterRetention) },
        }, ct)));
    }

    /// <summary>Puts a short-lived per-visitor marker; true if it wasn't there yet today.</summary>
    private async Task<bool> MarkAsync(PageVisit visit, string prefix, CancellationToken ct)
    {
        try
        {
            await db.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = new()
                {
                    ["PK"] = S($"VISITS#{Day(visit.Day)}"), ["SK"] = S(prefix + visit.VisitorKey),
                    ["ExpiresAt"] = Expiry(visit.Day, ShortLived),
                },
                ConditionExpression = "attribute_not_exists(PK)",
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }

    public async Task<IReadOnlyList<DailyVisits>> GetDaysAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1)) days.Add(d);
        return await Task.WhenAll(days.Select(async day =>
        {
            var counters = new Dictionary<string, int>();
            Dictionary<string, AttributeValue>? start = null;
            do
            {
                var res = await db.QueryAsync(new QueryRequest
                {
                    TableName = tableName,
                    KeyConditionExpression = "PK = :pk AND begins_with(SK, :c)",
                    ExpressionAttributeValues = new() { [":pk"] = S($"VISITS#{Day(day)}"), [":c"] = S("C#") },
                    ExclusiveStartKey = start,
                }, ct);
                foreach (var i in res.Items ?? []) counters[i["SK"].S[2..]] = int.Parse(i["Hits"].N, CultureInfo.InvariantCulture);
                start = res.LastEvaluatedKey is { Count: > 0 } k ? k : null;
            } while (start is not null);
            return VisitCounters.ToDay(day, counters);
        }));
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static AttributeValue S(string s) => new() { S = s };
    private static AttributeValue Expiry(DateOnly day, TimeSpan keep) => new()
    {
        N = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).Add(keep).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
    };
}
