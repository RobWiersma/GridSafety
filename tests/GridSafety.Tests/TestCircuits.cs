using GridSafety.Core;

namespace GridSafety.Tests;

/// <summary>
/// A synthetic straight east-west line (12 structures, 250 m spans) for geometry-sensitive tests,
/// so assertions about corridor offsets and distances don't depend on the bearing of real OSM lines.
/// </summary>
internal static class TestCircuits
{
    private static readonly GeoPoint Origin = new(38.0, -122.15);

    public static readonly Circuit Line = new("TEST-60", "Test 60 kV line", 60, 60,
        Enumerable.Range(0, 12).Select(i => new Structure($"TEST-60-{i + 1:000}", $"T-{i + 1:000}", Geo.Offset(Origin, 0, i * 250))).ToList());

    public static readonly ICircuitCatalog Catalog = new Single(Line);

    private sealed class Single(Circuit c) : ICircuitCatalog
    {
        public IReadOnlyList<Circuit> All { get; } = [c];
        public Circuit? Find(string id) => id.Equals(c.Id, StringComparison.OrdinalIgnoreCase) ? c : null;
    }
}
