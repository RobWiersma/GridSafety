using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace GridSafety.Core;

public enum DroneAssetStatus { Active, Maintenance, Retired }

/// <summary>A drone in the fleet. Live position and battery come from <see cref="DroneState"/>; this is the asset record.</summary>
public sealed record DroneAsset(string Id, string Model, string HomeBase, DroneAssetStatus Status = DroneAssetStatus.Active, string? Notes = null);

/// <summary>A flight crew: one or more remote pilots in command plus visual observers. Missions are flown under a pilot's name.</summary>
public sealed record FlightCrew(string Id, string Name, string HomeBase, IReadOnlyList<CrewMember> Members, CrewStatus Status = CrewStatus.Available)
{
    public const string PilotRole = "Remote pilot (PIC)";
    public const string ObserverRole = "Visual observer";

    /// <summary>Members who can be pilot in command.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<CrewMember> Pilots => Members.Where(m => m.Role == PilotRole);
}

/// <summary>
/// Everything the ops team schedules: drones, flight crews and ground crews. Stored as one small versioned
/// document, so an edit is a read-modify-write with optimistic concurrency.
/// </summary>
public sealed partial class ResourceCatalog
{
    public long Version { get; set; }
    public List<DroneAsset> Drones { get; init; } = [];
    public List<FlightCrew> FlightCrews { get; init; } = [];
    public List<Crew> GroundCrews { get; init; } = [];

    public DroneAsset? FindDrone(string id) => Drones.FirstOrDefault(d => d.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The demo's starting fleet and crews. Everyone is fictional.</summary>
    public static ResourceCatalog Default() => new()
    {
        Drones =
        [
            new("DEMO-01", "Simulated inspection quad", "Cloud", Notes: "Flies the on-demand demo flights."),
            new("DRN-01", "Inspection quad, zoom + thermal", "Concord yard"),
            new("DRN-02", "Inspection quad, zoom + thermal", "Concord yard"),
            new("DRN-03", "Long-range fixed wing, LiDAR", "Antioch yard"),
            new("DRN-04", "Inspection quad, zoom", "Walnut Creek yard", DroneAssetStatus.Maintenance, "Gimbal recalibration."),
            new("DRN-05", "Heavy-lift hexacopter, corona camera", "Pittsburg yard"),
            new("DRN-07", "Inspection quad, zoom + thermal", "Pittsburg yard"),
        ],
        FlightCrews =
        [
            new("air-1", "Springfield Air Ops 1", "Concord yard",
                [new("Edna Krabble", FlightCrew.PilotRole), new("Seymour Skinley", FlightCrew.ObserverRole)]),
            new("air-2", "Springfield Air Ops 2", "Antioch yard",
                [new("Kent Brockwell", FlightCrew.PilotRole), new("Troy McClaire", FlightCrew.ObserverRole)]),
            new("air-3", "Capital City Air Ops", "Pittsburg yard",
                [new("Patty Bouvay", FlightCrew.PilotRole), new("Selma Bouvay", FlightCrew.PilotRole), new("Hans Moleman", FlightCrew.ObserverRole)],
                CrewStatus.OffDuty),
        ],
        GroundCrews = [.. CrewRoster.All],
    };

    /// <summary>Checks one record before it is saved; returns field → messages (empty when valid).</summary>
    public static Dictionary<string, string[]> Validate(DroneAsset d)
    {
        var e = new Dictionary<string, string[]>();
        if (d.Id is not { Length: > 0 and <= 40 } || !DroneIdPattern().IsMatch(d.Id)) e["id"] = ["Drone ID: letters, digits and dashes, max 40."];
        if (Blank(d.Model, 80)) e["model"] = ["Model is required (max 80)."];
        if (Blank(d.HomeBase, 60)) e["homeBase"] = ["Home base is required (max 60)."];
        if (!Enum.IsDefined(d.Status)) e["status"] = ["Unknown status."];
        if (d.Notes?.Length > 200) e["notes"] = ["Notes: max 200 characters."];
        return e;
    }

    public static Dictionary<string, string[]> Validate(FlightCrew c)
    {
        var e = ValidateCrew(c.Id, c.Name, c.Members, c.Status);
        if (Blank(c.HomeBase, 60)) e["homeBase"] = ["Home base is required (max 60)."];
        if (!e.ContainsKey("members") && !c.Pilots.Any()) e["members"] = ["A flight crew needs at least one remote pilot (PIC)."];
        return e;
    }

    public static Dictionary<string, string[]> Validate(Crew c)
    {
        var e = ValidateCrew(c.Id, c.Name, c.Members, c.Status);
        if (Blank(c.Specialty, 80)) e["specialty"] = ["Specialty is required (max 80)."];
        if (c.Handles is null || c.Handles.Any(h => !Enum.IsDefined(h))) e["handles"] = ["Unknown issue type."];
        return e;
    }

    private static Dictionary<string, string[]> ValidateCrew(string id, string name, IReadOnlyList<CrewMember>? members, CrewStatus status)
    {
        var e = new Dictionary<string, string[]>();
        if (id is not { Length: > 0 and <= 40 } || !CrewIdPattern().IsMatch(id)) e["id"] = ["ID: lowercase letters, digits and dashes, max 40."];
        if (Blank(name, 80)) e["name"] = ["Name is required (max 80)."];
        if (!Enum.IsDefined(status)) e["status"] = ["Unknown status."];
        if (members is null || members.Count is 0 or > 12) e["members"] = ["A crew has 1 to 12 members."];
        else if (members.Any(m => Blank(m.Name, 60) || Blank(m.Role, 60))) e["members"] = ["Each member needs a name and a role (max 60 each)."];
        else if (members.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) e["members"] = ["Member names must be unique."];
        return e;
    }

    private static bool Blank(string? s, int max) => string.IsNullOrWhiteSpace(s) || s.Length > max;

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex DroneIdPattern();

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex CrewIdPattern();
}

public interface IResourceStore
{
    /// <summary>The current catalog, or the defaults (Version 0) if nobody has edited it yet.</summary>
    Task<ResourceCatalog> GetAsync(CancellationToken ct = default);

    /// <summary>Saves if nobody else saved since <see cref="ResourceCatalog.Version"/> was read; otherwise throws <see cref="ConcurrencyException"/>.</summary>
    Task SaveAsync(ResourceCatalog catalog, CancellationToken ct = default);
}

public sealed class InMemoryResourceStore : IResourceStore
{
    private readonly Lock _gate = new();
    private string? _json;

    public Task<ResourceCatalog> GetAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_json is null ? ResourceCatalog.Default() : JsonSerializer.Deserialize<ResourceCatalog>(_json, Json.Options)!);
    }

    public Task SaveAsync(ResourceCatalog catalog, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = _json is null ? 0 : JsonSerializer.Deserialize<ResourceCatalog>(_json, Json.Options)!.Version;
            if (current != catalog.Version) throw new ConcurrencyException("resources");
            catalog.Version++;
            _json = JsonSerializer.Serialize(catalog, Json.Options);
        }
        return Task.CompletedTask;
    }
}

/// <summary>PK=RESOURCES, SK=CATALOG. Not SK=META, so edits stay out of the domain-event stream.</summary>
public sealed class DynamoResourceStore(IAmazonDynamoDB db, string tableName) : IResourceStore
{
    private static Dictionary<string, AttributeValue> Key() => new() { ["PK"] = new() { S = "RESOURCES" }, ["SK"] = new() { S = "CATALOG" } };

    public async Task<ResourceCatalog> GetAsync(CancellationToken ct = default)
    {
        var res = await db.GetItemAsync(new GetItemRequest { TableName = tableName, Key = Key(), ConsistentRead = true }, ct);
        return res.Item is { Count: > 0 } item ? JsonSerializer.Deserialize<ResourceCatalog>(item["Doc"].S, Json.Options)! : ResourceCatalog.Default();
    }

    public async Task SaveAsync(ResourceCatalog catalog, CancellationToken ct = default)
    {
        var expected = catalog.Version;
        catalog.Version = expected + 1;
        var item = Key();
        item["EntityType"] = new() { S = "Resources" };
        item["Version"] = new() { N = catalog.Version.ToString(CultureInfo.InvariantCulture) };
        item["Doc"] = new() { S = JsonSerializer.Serialize(catalog, Json.Options) };
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
            catalog.Version = expected;
            throw new ConcurrencyException("resources");
        }
    }
}
