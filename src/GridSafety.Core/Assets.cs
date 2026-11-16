using System.Text.Json;

namespace GridSafety.Core;

public sealed record Structure(string Id, string Label, GeoPoint Location);

/// <summary>A distribution or transmission circuit: an ordered chain of poles/towers.</summary>
public sealed record Circuit(string Id, string Name, int VoltageKv, double CorridorMeters, IReadOnlyList<Structure> Structures)
{
    public IReadOnlyList<GeoPoint> Path => Structures.Select(s => s.Location).ToList();

    public double LengthMeters => Structures.Zip(Structures.Skip(1), (a, b) => Geo.DistanceMeters(a.Location, b.Location)).Sum();
}

public interface ICircuitCatalog
{
    IReadOnlyList<Circuit> All { get; }
    Circuit? Find(string id);
}

/// <summary>On-disk circuit dataset written by <c>tools/GridSafety.CircuitImport</c>.</summary>
public sealed record CircuitDataFile
{
    public required string Source { get; init; }
    public required string License { get; init; }
    public required string Attribution { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public double[] Bbox { get; init; } = [];
    public List<CircuitRecord> Circuits { get; init; } = [];
}

public sealed record CircuitRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>0 when OSM has no voltage for the line.</summary>
    public int VoltageKv { get; init; }

    /// <summary>True when some segments had no voltage tag and took it from the lines they connect to.</summary>
    public bool VoltageInferred { get; init; }

    public double CorridorMeters { get; init; }
    public List<long> OsmWayIds { get; init; } = [];
    public List<StructureRecord> Structures { get; init; } = [];
}

/// <param name="Kind">OSM <c>power=*</c> tag of the node: tower, pole, or "untagged".</param>
public sealed record StructureRecord(string Id, string Label, double Lat, double Lon, string Kind, long OsmNodeId);

/// <summary>
/// Real power lines and towers around Martinez and Pleasant Hill, CA, imported from OpenStreetMap
/// (© OpenStreetMap contributors, ODbL) and embedded in this assembly. In a utility this would
/// come from the GIS / asset registry on a schedule; here the importer plays that role.
/// Re-generate with: <c>dotnet run --project tools/GridSafety.CircuitImport</c>.
/// </summary>
public sealed class StaticCircuitCatalog : ICircuitCatalog
{
    public const string ResourceName = "GridSafety.Core.Data.circuits.json";

    public static readonly StaticCircuitCatalog Instance = new(LoadEmbedded());

    public StaticCircuitCatalog(CircuitDataFile data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        All = data.Circuits.Select(ToCircuit).ToList();
    }

    public CircuitDataFile Data { get; }

    public IReadOnlyList<Circuit> All { get; }

    public Circuit? Find(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static Circuit ToCircuit(CircuitRecord r) => new(
        r.Id, r.Name, r.VoltageKv, r.CorridorMeters,
        r.Structures.Select(s => new Structure(s.Id, s.Label, new GeoPoint(s.Lat, s.Lon))).ToList());

    private static CircuitDataFile LoadEmbedded()
    {
        using var stream = typeof(StaticCircuitCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing");
        return JsonSerializer.Deserialize<CircuitDataFile>(stream, Json.Options)
               ?? throw new InvalidOperationException("Circuit data file is empty");
    }
}
