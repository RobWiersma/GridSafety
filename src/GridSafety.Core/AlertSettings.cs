using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GridSafety.Core;

/// <summary>The kinds of email alert a supervisor can switch on or off.</summary>
public enum AlertType
{
    /// <summary>A new finding at or above the severity threshold.</summary>
    CriticalFinding,
    /// <summary>A finding at or above the threshold that nobody triaged before its SLA deadline.</summary>
    OverdueTriage,
    /// <summary>A critical in-flight drone alert (corridor breach, low battery, lost link).</summary>
    FlightAlert,
    /// <summary>A mission completed or was aborted.</summary>
    MissionComplete,
}

/// <summary>
/// Which events send email and to whom. Supervisors edit it in the console; the notifier Lambdas read it before
/// every page. Stored as one small versioned document, like <see cref="ResourceCatalog"/>.
/// </summary>
public sealed partial class AlertSettings
{
    public const int MaxRecipients = 10;

    public long Version { get; set; }
    public bool CriticalFindings { get; set; } = true;
    public bool OverdueTriage { get; set; } = true;
    public bool FlightAlerts { get; set; } = true;
    public bool MissionComplete { get; set; }

    /// <summary>Findings below this severity (1–5) never send email, new or overdue.</summary>
    public int MinSeverity { get; set; } = 4;

    /// <summary>Addresses subscribed to alert email, in addition to the ops address set at deploy time.</summary>
    public List<string> Recipients { get; set; } = [];

    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Matches what was paged before alerts were configurable: severity 4+ findings, SLA breaches and critical flight alerts.</summary>
    public static AlertSettings Default() => new();

    public bool IsEnabled(AlertType type) => type switch
    {
        AlertType.CriticalFinding => CriticalFindings,
        AlertType.OverdueTriage => OverdueTriage,
        AlertType.FlightAlert => FlightAlerts,
        AlertType.MissionComplete => MissionComplete,
        _ => false,
    };

    /// <summary>Whether an alert of this type (and, for findings, this severity) should send email.</summary>
    public bool Allows(AlertType type, int? severity = null) =>
        IsEnabled(type) && type switch
        {
            AlertType.CriticalFinding or AlertType.OverdueTriage => (severity ?? 0) >= MinSeverity,
            _ => true,
        };

    /// <summary>Trims and lowercases addresses and drops blanks and duplicates.</summary>
    public static List<string> NormalizeRecipients(IEnumerable<string?>? recipients) =>
        (recipients ?? [])
            .Select(r => r?.Trim().ToLowerInvariant())
            .Where(r => !string.IsNullOrEmpty(r))
            .Distinct()
            .ToList()!;

    /// <summary>Checks settings before they are saved; returns field → messages (empty when valid).</summary>
    public static Dictionary<string, string[]> Validate(AlertSettings s)
    {
        var e = new Dictionary<string, string[]>();
        if (s.MinSeverity is < 1 or > 5) e["minSeverity"] = ["Severity threshold must be 1 to 5."];
        if (s.Recipients is null) e["recipients"] = ["Recipients are required (may be empty)."];
        else if (s.Recipients.Count > MaxRecipients) e["recipients"] = [$"At most {MaxRecipients} recipients."];
        else if (s.Recipients.FirstOrDefault(r => r.Length > 254 || !EmailPattern().IsMatch(r)) is { } bad)
            e["recipients"] = [$"Not a valid email address: {bad}"];
        return e;
    }

    [GeneratedRegex(@"^[^@\s,;<>""]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}$")]
    private static partial Regex EmailPattern();
}

public interface IAlertSettingsStore
{
    /// <summary>The current settings, or the defaults (Version 0) if nobody has edited them yet.</summary>
    Task<AlertSettings> GetAsync(CancellationToken ct = default);

    /// <summary>Saves if nobody else saved since <see cref="AlertSettings.Version"/> was read; otherwise throws <see cref="ConcurrencyException"/>.</summary>
    Task SaveAsync(AlertSettings settings, CancellationToken ct = default);
}

public sealed class InMemoryAlertSettingsStore : IAlertSettingsStore
{
    private readonly Lock _gate = new();
    private string? _json;

    public Task<AlertSettings> GetAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_json is null ? AlertSettings.Default() : JsonSerializer.Deserialize<AlertSettings>(_json, Json.Options)!);
    }

    public Task SaveAsync(AlertSettings settings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = _json is null ? 0 : JsonSerializer.Deserialize<AlertSettings>(_json, Json.Options)!.Version;
            if (current != settings.Version) throw new ConcurrencyException("alert settings");
            settings.Version++;
            _json = JsonSerializer.Serialize(settings, Json.Options);
        }
        return Task.CompletedTask;
    }
}

/// <summary>PK=SETTINGS, SK=ALERTS. Not SK=META, so edits stay out of the domain-event stream.</summary>
public sealed class DynamoAlertSettingsStore(IAmazonDynamoDB db, string tableName) : IAlertSettingsStore
{
    private static Dictionary<string, AttributeValue> Key() => new() { ["PK"] = new() { S = "SETTINGS" }, ["SK"] = new() { S = "ALERTS" } };

    public async Task<AlertSettings> GetAsync(CancellationToken ct = default)
    {
        var res = await db.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Key(), ConsistentRead = true }, ct);
        return res.Item is { Count: > 0 } item ? JsonSerializer.Deserialize<AlertSettings>(item["Doc"].S, Json.Options)! : AlertSettings.Default();
    }

    public async Task SaveAsync(AlertSettings settings, CancellationToken ct = default)
    {
        var expected = settings.Version;
        settings.Version = expected + 1;
        var item = Key();
        item["EntityType"] = new() { S = "AlertSettings" };
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
            throw new ConcurrencyException("alert settings");
        }
    }
}
