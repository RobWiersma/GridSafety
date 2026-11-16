using GridSafety.CircuitImport;
using GridSafety.Core;

namespace GridSafety.Tests;

public class CircuitImporterTests
{
    private static readonly GeoPoint O = new(38.0, -122.15);
    private static readonly double[] WideArea = [37.9, -122.3, 38.1, -122.0];

    private static OsmElement Node(long id, double eastM, string? power = "tower")
    {
        var p = Geo.Offset(O, 0, eastM);
        return new OsmElement("node", id, p.Lat, p.Lon, null, power is null ? null : new() { ["power"] = power });
    }

    private static OsmElement Town(long id, string name, double northM, double eastM)
    {
        var p = Geo.Offset(O, northM, eastM);
        return new OsmElement("node", id, p.Lat, p.Lon, null, new() { ["place"] = "town", ["name"] = name });
    }

    private static OsmElement Way(long id, string? voltage, string? name, params long[] nodes) => Way(id, voltage, name, "line", nodes);

    private static OsmElement Way(long id, string? voltage, string? name, string power, params long[] nodes)
    {
        var tags = new Dictionary<string, string> { ["power"] = power, ["name"] = name ?? "" };
        if (voltage is not null) tags["voltage"] = voltage;
        return new("way", id, null, null, [.. nodes], tags);
    }

    /// <summary>Towers <paramref name="from"/>..<paramref name="to"/> (inclusive), 200 m apart along an east-west line.</summary>
    private static List<OsmElement> Towers(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => Node(100 + i, i * 200)).ToList();

    private static long[] Ids(IEnumerable<OsmElement> nodes) => nodes.Select(n => n.Id).ToArray();

    [Theory]
    [InlineData("115000", 115)]
    [InlineData("230000;115000", 230)]
    [InlineData("60000", 60)]
    [InlineData("medium", 0)]
    public void Parses_voltage(string tag, int kv) => Assert.Equal(kv, CircuitBuilder.Kv(Way(1, tag, null, 1, 2)));

    [Fact]
    public void Stitches_reversed_segments_into_one_chain()
    {
        // 1-2-3 then segment drawn backwards 5-4-3
        var chains = CircuitBuilder.Stitch([Way(10, "115000", "A", 1, 2, 3), Way(11, "115000", "A", 5, 4, 3)]);
        var (ways, nodes) = Assert.Single(chains);
        Assert.Equal(2, ways.Count);
        Assert.True(nodes.SequenceEqual([1L, 2, 3, 4, 5]) || nodes.SequenceEqual([5L, 4, 3, 2, 1]));
    }

    [Fact]
    public void Does_not_stitch_through_a_junction()
    {
        // Three lines meet at node 3 (a substation): they must stay separate circuits.
        var chains = CircuitBuilder.Stitch([Way(10, "115000", "A", 1, 2, 3), Way(11, "115000", "B", 3, 4, 5), Way(12, "115000", "C", 3, 6, 7)]);
        Assert.Equal(3, chains.Count);
    }

    [Fact]
    public void Builds_named_circuit_and_thins_duplicate_vertices()
    {
        var nodes = Towers(0, 13);
        nodes.Add(Node(200, 13 * 200 + 5, power: null));             // 5 m past the last tower: dropped
        var way = Way(1, "115000", "Pittsburg - Martinez", Ids(nodes));

        var circuit = Assert.Single(CircuitBuilder.Build(new OverpassResponse([way, .. nodes]), WideArea));
        Assert.Equal("PITT-MART-115", circuit.Id);
        Assert.Equal("Pittsburg - Martinez 115 kV", circuit.Name);
        Assert.Equal(75, circuit.CorridorMeters);
        Assert.False(circuit.VoltageInferred);
        Assert.Equal(14, circuit.Structures.Count);
        Assert.Equal("PM-001", circuit.Structures[0].Label);
        Assert.Equal(100, circuit.Structures[0].OsmNodeId);
    }

    [Fact]
    public void Clips_to_area_and_drops_lines_left_too_short()
    {
        var nodes = Towers(0, 29);
        var way = Way(1, "60000", null, Ids(nodes));
        var eastEdge = Geo.Offset(O, 0, 11 * 200 + 50).Lon;              // keeps towers 0..11
        var c = Assert.Single(CircuitBuilder.Build(new OverpassResponse([way, .. nodes]), [37.9, -122.3, 38.1, eastEdge]));
        Assert.Equal(12, c.Structures.Count);
        Assert.Equal("LINE-60-A", c.Id);                                  // no towns in the data

        var twoTowers = CircuitBuilder.Build(new OverpassResponse([way, .. nodes]), [37.9, -122.3, 38.1, Geo.Offset(O, 0, 1 * 200 + 50).Lon]);
        Assert.Empty(twoTowers);
    }

    [Fact]
    public void Drops_substation_stubs_unless_asked_to_keep_them()
    {
        var nodes = Towers(0, 1);                                          // 200 m hookup
        var response = new OverpassResponse([Way(1, "115000", null, Ids(nodes)), .. nodes]);

        Assert.Empty(CircuitBuilder.Build(response, WideArea, minStructures: 2));
        Assert.Single(CircuitBuilder.Build(response, WideArea, minStructures: 2, minLengthM: 0));
    }

    [Fact]
    public void Names_unnamed_lines_after_the_nearest_town()
    {
        var nodes = Towers(0, 9);
        var response = new OverpassResponse([
            Way(1, "60000", null, Ids(nodes)), .. nodes,
            Town(900, "Pleasant Hill", 300, 900),                         // right beside the middle of the line
            Town(901, "Martinez", 8000, 0),
        ]);

        var c = Assert.Single(CircuitBuilder.Build(response, WideArea));
        Assert.Equal("PH-60-A", c.Id);
        Assert.Equal("Pleasant Hill 60 kV line A", c.Name);
        Assert.Equal("PA-001", c.Structures[0].Label);
    }

    [Theory]
    [InlineData("Pleasant Hill", "PH")]
    [InlineData("Martinez", "MART")]
    [InlineData(null, "LINE")]
    public void Town_codes(string? town, string code) => Assert.Equal(code, CircuitBuilder.TownCode(town));

    [Fact]
    public void Untagged_segment_takes_the_voltage_of_the_line_it_continues()
    {
        // 115 kV way 1→2→3, then an untagged way 3→4→5, then another untagged 5→6 (inference flows along).
        var ways = new List<OsmElement> { Way(1, "115000", "A", 1, 2, 3), Way(2, null, null, 3, 4, 5), Way(3, null, null, 5, 6) };
        var kv = CircuitBuilder.Voltages(ways);

        Assert.Equal((115, false), kv[1]);
        Assert.Equal((115, true), kv[2]);
        Assert.Equal((115, true), kv[3]);
    }

    [Fact]
    public void Voltage_stays_unknown_when_neighbours_disagree_or_line_is_distribution()
    {
        var ways = new List<OsmElement>
        {
            Way(1, "115000", "A", 1, 2), Way(2, "60000", "B", 3, 4),
            Way(3, null, null, 2, 9, 3),                                   // joins a 115 kV and a 60 kV line
            Way(4, null, null, "minor_line", 4, 7),                       // distribution: never inferred
        };
        var kv = CircuitBuilder.Voltages(ways);

        Assert.Equal((0, false), kv[3]);
        Assert.Equal((0, false), kv[4]);
    }

    [Fact]
    public void Unknown_voltage_line_is_kept_and_labelled()
    {
        var nodes = Towers(0, 9);
        var c = Assert.Single(CircuitBuilder.Build(new OverpassResponse([Way(1, null, null, Ids(nodes)), .. nodes, Town(900, "Martinez", 0, 0)]), WideArea));

        Assert.Equal("MART-UNK-A", c.Id);
        Assert.Equal("Martinez line A (voltage not tagged)", c.Name);
        Assert.Equal(0, c.VoltageKv);
        Assert.Equal(60, c.CorridorMeters);
    }

    [Fact]
    public void Same_name_split_at_a_junction_gets_numbered_sections()
    {
        // Two runs of the same named line meeting a third line at node 109 (a junction).
        var a = Towers(0, 9);
        var b = Towers(9, 19);
        var spur = new[] { Node(500, 9 * 200 + 1, null) };
        var response = new OverpassResponse([
            Way(1, "115000", "Oleum - Martinez", Ids(a)),
            Way(2, "115000", "Oleum - Martinez", Ids(b)),
            Way(3, "115000", "Spur", 109, 500),
            .. a, .. b.Skip(1), .. spur,
        ]);

        var built = CircuitBuilder.Build(response, WideArea);
        Assert.Equal(["Oleum - Martinez 115 kV", "Oleum - Martinez 115 kV, section 2"], built.Select(c => c.Name).Order().ToArray());
        Assert.Contains(built, c => c.Id == "OLEU-MART-115-2");
    }

    [Fact]
    public void Scrub_removes_operator_and_owner_tags_but_keeps_line_data()
    {
        const string raw = """
            {"elements":[{"type":"way","id":1,"nodes":[1,2],"tags":{"power":"line","voltage":"115000","name":"A - B",
              "operator":"Some Utility","operator:short":"SU","operator:wikidata":"Q1","owner":"Some Utility"}}]}
            """;
        var tags = System.Text.Json.Nodes.JsonNode.Parse(OsmScrub.RemoveOwnershipTags(raw))!["elements"]![0]!["tags"]!.AsObject();

        Assert.Equal(["power", "voltage", "name"], tags.Select(t => t.Key).ToArray());
    }

    [Fact]
    public void Committed_snapshot_names_no_operator_or_owner()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSafety.slnx"))) dir = dir.Parent;
        var snapshot = Path.Combine(dir!.FullName, "tools", "GridSafety.CircuitImport", "data", "contra-costa-overpass.json");

        var keys = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(snapshot))!["elements"]!.AsArray()
            .SelectMany(e => e?["tags"]?.AsObject().Select(t => t.Key) ?? []);
        Assert.DoesNotContain(keys, OsmScrub.IsOwnershipTag);
    }
}
