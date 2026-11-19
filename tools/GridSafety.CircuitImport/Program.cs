// GridSafety circuit importer: pulls real power lines + towers from OpenStreetMap (Overpass API)
// and writes the circuit dataset the platform embeds (src/GridSafety.Core/Data/circuits.json).
//
//   dotnet run --project tools/GridSafety.CircuitImport                       # Contra Costa County, CA (default)
//   dotnet run --project tools/GridSafety.CircuitImport -- --area none --bbox 37.92,-122.19,38.05,-122.03   # just Martinez + Pleasant Hill
//   dotnet run --project tools/GridSafety.CircuitImport -- --input saved-overpass.json   # offline / reproducible

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSafety.CircuitImport;
using GridSafety.Core;

var argList = args.ToList();
string? Val(string name) => argList.IndexOf(name) is var i and >= 0 && i + 1 < argList.Count ? argList[i + 1] : null;

if (argList.Contains("--help"))
{
    Console.WriteLine("""
        Options:
          --area <names>   Import lines inside administrative boundaries, e.g. "Contra Costa County" (default), or several
                           separated by commas: "Contra Costa County,Alameda County,Solano County".
                           Lines crossing the boundary are clipped to --bbox.
          --bbox s,w,n,e   Clip box, or the whole area to import with --area none
                           (default: Contra Costa County, CA 37.71,-122.44,38.10,-121.53)
          --max <n>        Keep only the n longest circuits (default 0 = all)
          --min <n>        Drop lines with fewer structures inside the area (default 3)
          --min-length <m> Drop lines shorter than m meters, e.g. substation hookups (default 400; 0 keeps all)
          --input <file>   Use a saved Overpass JSON response instead of calling the API
          --save-raw <f>   Also save the Overpass response (operator/owner tags removed)
          --out <file>     Output (default src/GridSafety.Core/Data/circuits.json)
        """);
    return 0;
}

var bbox = (Val("--bbox") ?? "37.71,-122.44,38.10,-121.53").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
if (bbox.Length != 4) throw new ArgumentException("--bbox needs s,w,n,e");
var area = Val("--area") is { } a ? (a.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : a) : "Contra Costa County";
var max = int.Parse(Val("--max") ?? "0", CultureInfo.InvariantCulture);
var minStructures = int.Parse(Val("--min") ?? CircuitBuilder.DefaultMinStructures.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
var minLengthM = double.Parse(Val("--min-length") ?? CircuitBuilder.DefaultMinLengthM.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
var outPath =Val("--out") ?? Path.Combine(FindRepoRoot(), "src", "GridSafety.Core", "Data", "circuits.json");

string raw;
if (Val("--input") is { } input)
{
    raw = await File.ReadAllTextAsync(input);
    Console.WriteLine($"Read {input}");
}
else
{
    raw = await Overpass.FetchAsync(bbox, area);
}

raw = OsmScrub.RemoveOwnershipTags(raw);
if (Val("--save-raw") is { } saveRaw) await File.WriteAllTextAsync(saveRaw, raw);

var response = JsonSerializer.Deserialize<OverpassResponse>(raw, OverpassResponse.Options)
               ?? throw new InvalidOperationException("Empty Overpass response");
var circuits = CircuitBuilder.Build(response, bbox, max, minStructures, minLengthM);

var file = new CircuitDataFile
{
    Source = area is null
        ? "OpenStreetMap via Overpass API: power=line|minor_line and place=city|town|village in the bounding box"
        : $"OpenStreetMap via Overpass API: power=line|minor_line and place=city|town|village in {area}, clipped to the bounding box",
    License = "ODbL 1.0 (https://opendatacommons.org/licenses/odbl/)",
    Attribution = "© OpenStreetMap contributors",
    GeneratedAt = DateTimeOffset.UtcNow,
    Bbox = bbox,
    Circuits = circuits,
};
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(file, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));

Console.WriteLine($"Wrote {circuits.Count} circuits -> {outPath}");
foreach (var c in circuits)
{
    var km = c.Structures.Zip(c.Structures.Skip(1), (a, b) => Geo.DistanceMeters(new(a.Lat, a.Lon), new(b.Lat, b.Lon))).Sum() / 1000;
    var kv = c.VoltageKv > 0 ? $"{c.VoltageKv,4} kV{(c.VoltageInferred ? "*" : " ")}" : "   ? kV ";
    Console.WriteLine($"  {c.Id,-18} {c.Name,-52} {kv} {c.Structures.Count,3} structures  {km,5:0.0} km");
}
if (circuits.Any(c => c.VoltageInferred)) Console.WriteLine("  * voltage filled in from connected segments");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSafety.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

