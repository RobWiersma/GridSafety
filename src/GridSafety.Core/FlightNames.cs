namespace GridSafety.Core;

/// <summary>Callsigns for demo flights, e.g. "Amber Kestrel 07": 24 × 24 × 99 ≈ 57,000 combinations.</summary>
public static class FlightNames
{
    private static readonly string[] Adjectives =
    [
        "Amber", "Bold", "Bright", "Brisk", "Clear", "Copper", "Crimson", "Golden", "High", "Iron", "Keen", "Lucky",
        "Nimble", "Quick", "Quiet", "Rapid", "Sharp", "Silver", "Steady", "Sunny", "True", "Vivid", "Wild", "Zephyr",
    ];

    private static readonly string[] Birds =
    [
        "Condor", "Crane", "Egret", "Falcon", "Finch", "Gannet", "Harrier", "Hawk", "Heron", "Ibis", "Kestrel", "Kite",
        "Lark", "Merlin", "Osprey", "Owl", "Petrel", "Plover", "Raven", "Robin", "Sparrow", "Swift", "Tern", "Wren",
    ];

    /// <summary>A random callsign not in <paramref name="taken"/> (case-insensitive).</summary>
    public static string Generate(Random rng, IEnumerable<string?> taken)
    {
        ArgumentNullException.ThrowIfNull(rng);
        var used = new HashSet<string>(taken.OfType<string>(), StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var name = $"{Adjectives[rng.Next(Adjectives.Length)]} {Birds[rng.Next(Birds.Length)]} {rng.Next(1, 100):00}";
            if (used.Add(name)) return name;
        }
        // Practically unreachable; fall back to something that can't collide.
        return $"Flight {Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
    }
}
