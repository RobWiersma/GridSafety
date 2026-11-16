using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GridSafety.Core;

namespace GridSafety.CircuitImport
{
    internal static class Overpass
    {
        private static readonly string[] Endpoints =
        [
            "https://overpass-api.de/api/interpreter",
            "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
        ];

        /// <summary>Power lines plus their nodes, and settlement names used to label unnamed lines.</summary>
        public static string Query(double[] b)
        {
            var box = string.Create(CultureInfo.InvariantCulture, $"{b[0]},{b[1]},{b[2]},{b[3]}");
            return $"[out:json][timeout:90];(way[\"power\"~\"^(line|minor_line)$\"]({box});node[\"place\"~\"^(city|town|village)$\"]({box}););out body;>;out body qt;";
        }

        public static async Task<string> FetchAsync(double[] bbox)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GridSafety-CircuitImport/1.0");
            foreach (var url in Endpoints)
            {
                try
                {
                    Console.WriteLine($"Querying {url} …");
                    using var res = await http.PostAsync(url, new FormUrlEncodedContent([new("data", Query(bbox))]));
                    res.EnsureSuccessStatusCode();
                    return await res.Content.ReadAsStringAsync();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine($"  failed: {ex.Message}");
                }
            }
            throw new InvalidOperationException("All Overpass endpoints failed. Save a response and pass --input.");
        }
    }

    /// <summary>
    /// Removes who-owns/operates-it tags (<c>operator*</c>, <c>owner*</c>) from an Overpass response before it is
    /// parsed or saved. GridSafety only needs geometry, voltage and line names, so no utility is named in the repo.
    /// </summary>
    internal static class OsmScrub
    {
        public static bool IsOwnershipTag(string key) =>
            key.StartsWith("operator", StringComparison.Ordinal) || key.StartsWith("owner", StringComparison.Ordinal);

        public static string RemoveOwnershipTags(string overpassJson)
        {
            var root = JsonNode.Parse(overpassJson) ?? throw new InvalidOperationException("Empty Overpass response");
            foreach (var tags in root["elements"]?.AsArray().Select(e => e?["tags"]?.AsObject()).OfType<JsonObject>() ?? [])
            {
                foreach (var key in tags.Select(t => t.Key).Where(IsOwnershipTag).ToList()) tags.Remove(key);
            }
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }

    internal sealed record OverpassResponse(List<OsmElement> Elements)
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }

    internal sealed record OsmElement(
        string Type,
        long Id,
        double? Lat,
        double? Lon,
        [property: JsonPropertyName("nodes")] List<long>? Nodes,
        Dictionary<string, string>? Tags)
    {
        public string? Tag(string key) => Tags is not null && Tags.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
    }

    internal sealed record Place(string Name, GeoPoint Location);

    /// <summary>
    /// Turns OSM line segments into flyable circuits:
    /// 1. fill in missing voltages from connected lines, 2. group ways by voltage, 3. stitch segments
    /// end-to-end (only through degree-2 joints, so lines meeting at a substation stay separate),
    /// 4. clip to the area, 5. drop vertices closer than <see cref="MinSpacingM"/> (duplicate gantry nodes),
    /// 6. name them from OSM tags, or after the nearest town when the line has no name.
    /// </summary>
    internal static class CircuitBuilder
    {
        public const double MinSpacingM = 20;
        public const int DefaultMinStructures = 3;

        /// <summary>Shorter pieces are almost always substation bus/gantry hookups, not lines a drone would patrol.</summary>
        public const double DefaultMinLengthM = 400;

        /// <param name="max">Keep only the longest <paramref name="max"/> circuits; 0 keeps all.</param>
        public static List<CircuitRecord> Build(OverpassResponse response, double[] bbox, int max = 0,
            int minStructures = DefaultMinStructures, double minLengthM = DefaultMinLengthM)
        {
            var nodes = response.Elements.Where(e => e is { Type: "node", Lat: not null, Lon: not null }).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
            var ways = response.Elements.Where(e => e.Type == "way" && e.Nodes is { Count: >= 2 }).ToList();
            var places = response.Elements
                .Where(e => e is { Type: "node", Lat: not null, Lon: not null } && e.Tag("place") is "city" or "town" or "village" && e.Tag("name") is not null)
                .Select(e => new Place(e.Tag("name")!, new GeoPoint(e.Lat!.Value, e.Lon!.Value)))
                .ToList();
            var voltages = Voltages(ways);

            var candidates = new List<(List<OsmElement> Ways, List<OsmElement> Nodes, int Kv, double Length)>();
            foreach (var group in ways.GroupBy(w => voltages[w.Id].Kv))
            {
                foreach (var (chainWays, chainNodeIds) in Stitch(group.ToList()))
                {
                    var inside = LongestRunInside(chainNodeIds.Where(nodes.ContainsKey).Select(id => nodes[id]).ToList(), bbox);
                    var spaced = Thin(inside);
                    var length = PathLength(spaced);
                    if (spaced.Count < minStructures || length < minLengthM) continue;
                    candidates.Add((chainWays, spaced, group.Key, length));
                }
            }

            var ordered = candidates.OrderByDescending(c => c.Length).ToList();
            if (max > 0) ordered = ordered.Take(max).ToList();

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unnamedCount = new Dictionary<string, int>();
            var result = new List<CircuitRecord>();
            foreach (var (chainWays, chainNodes, kv, _) in ordered)
            {
                var kvCode = kv > 0 ? kv.ToString(CultureInfo.InvariantCulture) : "UNK";
                var name = Majority(chainWays, w => w.Tag("name") ?? w.Tag("ref"));
                string id, display, prefix;
                if (name is not null)
                {
                    var words = Words(name).Where(w => char.IsLetter(w[0])).Take(2).ToList();
                    id = string.Join('-', words.Select(w => w[..Math.Min(4, w.Length)].ToUpperInvariant())) + $"-{kvCode}";
                    display = kv > 0 ? $"{name} {kv} kV" : $"{name} (voltage not tagged)";
                    prefix = string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
                }
                else
                {
                    var town = Nearest(places, P(chainNodes[chainNodes.Count / 2]));
                    var townCode = TownCode(town);
                    var key = $"{townCode}-{kvCode}";
                    var n = unnamedCount[key] = unnamedCount.GetValueOrDefault(key) + 1;
                    var letter = n <= 26 ? ((char)('A' + n - 1)).ToString() : n.ToString(CultureInfo.InvariantCulture);
                    id = $"{key}-{letter}";
                    display = kv > 0 ? $"{town ?? "Unnamed"} {kv} kV line {letter}" : $"{town ?? "Unnamed"} line {letter} (voltage not tagged)";
                    prefix = $"{townCode[0]}{letter}";
                }
                var unique = id;
                var section = 1;
                while (!ids.Add(unique)) unique = $"{id}-{++section}"; // same line split at a junction
                if (section > 1) display = $"{display}, section {section}";

                result.Add(new CircuitRecord
                {
                    Id = unique,
                    Name = display,
                    VoltageKv = kv,
                    VoltageInferred = chainWays.Any(w => voltages[w.Id].Inferred),
                    CorridorMeters = kv >= 200 ? 100 : kv >= 100 ? 75 : 60,
                    OsmWayIds = chainWays.Select(w => w.Id).ToList(),
                    Structures = chainNodes.Select((n, i) => new StructureRecord(
                        $"{unique}-{i + 1:000}", $"{prefix}-{i + 1:000}", n.Lat!.Value, n.Lon!.Value, n.Tag("power") ?? "untagged", n.Id)).ToList(),
                });
            }
            return result;
        }

        /// <summary>Voltage in kV from tags like "115000" or "115000;60000" (first circuit wins); 0 when missing.</summary>
        internal static int Kv(OsmElement way) =>
            way.Tag("voltage")?.Split(';')[0] is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var volts)
                ? (int)Math.Round(volts / 1000)
                : 0;

        /// <summary>
        /// Many OSM segments are missing their <c>voltage</c> tag even though they continue a tagged line.
        /// An untagged <c>power=line</c> takes the voltage of the lines it joins end-to-end, but only when they
        /// all agree (a segment touching both a 60 kV and a 115 kV line stays unknown). Repeats so the value
        /// flows along runs of untagged segments. <c>minor_line</c> is distribution and is never inferred.
        /// </summary>
        internal static Dictionary<long, (int Kv, bool Inferred)> Voltages(List<OsmElement> ways)
        {
            var result = ways.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => (Kv: Kv(g.First()), Inferred: false));
            var byEndpoint = ways.SelectMany(w => new[] { (Node: w.Nodes![0], Way: w), (Node: w.Nodes[^1], Way: w) })
                                 .ToLookup(x => x.Node, x => x.Way);
            bool changed;
            do
            {
                changed = false;
                foreach (var w in ways.Where(w => result[w.Id].Kv == 0 && w.Tag("power") == "line"))
                {
                    var touching = new[] { w.Nodes![0], w.Nodes[^1] }
                        .SelectMany(n => byEndpoint[n])
                        .Where(o => o.Id != w.Id && o.Tag("power") == "line" && result[o.Id].Kv > 0)
                        .Select(o => result[o.Id].Kv)
                        .Distinct()
                        .ToList();
                    if (touching.Count != 1) continue;
                    result[w.Id] = (touching[0], true);
                    changed = true;
                }
            } while (changed);
            return result;
        }

        internal static List<(List<OsmElement> Ways, List<long> Nodes)> Stitch(List<OsmElement> ways)
        {
            // Endpoint degree: a joint shared by exactly two segments is a splice; more is a junction.
            var degree = ways.SelectMany(w => new[] { w.Nodes![0], w.Nodes[^1] }).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            var unused = new HashSet<OsmElement>(ways);
            var chains = new List<(List<OsmElement>, List<long>)>();

            while (unused.Count > 0)
            {
                var seed = unused.First();
                unused.Remove(seed);
                var chainWays = new List<OsmElement> { seed };
                var chain = new LinkedList<long>(seed.Nodes!);

                bool Extend(bool atTail)
                {
                    var joint = atTail ? chain.Last!.Value : chain.First!.Value;
                    if (degree[joint] != 2) return false;
                    var next = unused.FirstOrDefault(w => w.Nodes![0] == joint || w.Nodes[^1] == joint);
                    if (next is null) return false;
                    unused.Remove(next);
                    chainWays.Add(next);
                    var seq = next.Nodes![0] == joint ? next.Nodes : Enumerable.Reverse(next.Nodes).ToList();
                    if (atTail) foreach (var n in seq.Skip(1)) chain.AddLast(n);
                    else foreach (var n in seq.Skip(1)) chain.AddFirst(n);
                    return true;
                }

                while (Extend(atTail: true)) { }
                while (Extend(atTail: false)) { }
                chains.Add((chainWays, chain.ToList()));
            }
            return chains;
        }

        internal static List<OsmElement> LongestRunInside(List<OsmElement> nodes, double[] b)
        {
            List<OsmElement> best = [], run = [];
            foreach (var n in nodes)
            {
                if (n.Lat >= b[0] && n.Lat <= b[2] && n.Lon >= b[1] && n.Lon <= b[3]) run.Add(n);
                else run = [];
                if (run.Count > best.Count) best = [.. run];
            }
            return best;
        }

        internal static List<OsmElement> Thin(List<OsmElement> nodes)
        {
            var kept = new List<OsmElement>();
            foreach (var n in nodes)
            {
                if (kept.Count == 0 || Geo.DistanceMeters(P(kept[^1]), P(n)) >= MinSpacingM) kept.Add(n);
            }
            return kept;
        }

        /// <summary>"Pleasant Hill" → "PH", "Martinez" → "MART", no town → "LINE".</summary>
        internal static string TownCode(string? town)
        {
            if (town is null) return "LINE";
            var words = Words(town).Where(w => char.IsLetter(w[0])).ToList();
            return words.Count > 1
                ? string.Concat(words.Select(w => char.ToUpperInvariant(w[0])))
                : words[0][..Math.Min(4, words[0].Length)].ToUpperInvariant();
        }

        private static string? Nearest(List<Place> places, GeoPoint p) =>
            places.OrderBy(pl => Geo.DistanceMeters(pl.Location, p)).Select(pl => pl.Name).FirstOrDefault();

        private static string[] Words(string s) => s.Split([' ', '-', '/', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

        private static GeoPoint P(OsmElement n) => new(n.Lat!.Value, n.Lon!.Value);

        private static double PathLength(List<OsmElement> n) => n.Zip(n.Skip(1), (a, b) => Geo.DistanceMeters(P(a), P(b))).Sum();

        private static string? Majority(List<OsmElement> ways, Func<OsmElement, string?> pick) =>
            ways.Where(w => pick(w) is not null).GroupBy(pick).OrderByDescending(g => g.Sum(w => w.Nodes!.Count)).Select(g => g.Key).FirstOrDefault();
    }
}
