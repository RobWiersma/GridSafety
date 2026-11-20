using System.Runtime.CompilerServices;
using GridSafety.Core.Simulation;

namespace GridSafety.Core;

/// <summary>
/// One flight's stretch of a line: structures <see cref="FirstIndex"/> to <see cref="LastIndex"/> (inclusive), flight
/// <see cref="Number"/> of <see cref="Of"/>. Neighbouring stretches share their end structure, where the crew lands, swaps the
/// battery and launches again. <see cref="Minutes"/> is the expected time in the air, takeoff and landing included.
/// </summary>
public sealed record CircuitLeg(int Number, int Of, int FirstIndex, int LastIndex, string FromLabel, string ToLabel, int Structures,
    double LengthMeters, double Minutes);

/// <summary>
/// Splits lines into flights a drone can fly on one battery. A battery is good for about <see cref="Endurance"/> in
/// the air; <see cref="Overhead"/> of that goes on climbing out, landing and the reserve, and the rest is spent flying
/// the line at <see cref="FlightPlan.SpeedMps"/>. A line longer than that is split into near-equal stretches at structures.
/// </summary>
public static class FlightLegs
{
    /// <summary>Flight time on one battery.</summary>
    public static readonly TimeSpan Endurance = TimeSpan.FromMinutes(20);

    /// <summary>Time per flight not spent along the line: climbing out, landing, and a reserve.</summary>
    public static readonly TimeSpan Overhead = TimeSpan.FromMinutes(2);

    /// <summary>The most line one flight covers (about 15 km).</summary>
    public static double MaxLegMeters => FlightPlan.SpeedMps * (Endurance - Overhead).TotalSeconds;

    /// <summary>Expected time in the air for a stretch of line this long, takeoff and landing included.</summary>
    public static double MinutesFor(double lengthMeters) => Math.Round(lengthMeters / FlightPlan.SpeedMps / 60 + Overhead.TotalMinutes, 1);

    private static readonly ConditionalWeakTable<Circuit, IReadOnlyList<CircuitLeg>> Cache = new();

    /// <summary>The flights it takes to inspect the whole line, in order along it.</summary>
    public static IReadOnlyList<CircuitLeg> Legs(this Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        return Cache.GetValue(circuit, Split);
    }

    /// <summary>The leg numbered <paramref name="number"/>, or null when there's no such flight.</summary>
    public static CircuitLeg? Leg(this Circuit circuit, int? number) =>
        number is { } n && n >= 1 && n <= circuit.Legs().Count ? circuit.Legs()[n - 1] : null;

    /// <summary>
    /// The part of the line flight <paramref name="number"/> covers, as a circuit of its own (same id and name), or the whole
    /// line when <paramref name="number"/> is null (missions planned before lines were split).
    /// </summary>
    public static Circuit Stretch(this Circuit circuit, int? number) => circuit.Leg(number) is { } leg
        ? circuit with { Structures = [.. circuit.Structures.Skip(leg.FirstIndex).Take(leg.LastIndex - leg.FirstIndex + 1)] }
        : circuit;

    /// <summary>
    /// Which flight of a line to send next: one never inspected, first along the line, otherwise the one inspected longest
    /// ago. Flights already planned or in the air are skipped while anything else is left. A mission with no flight
    /// number (from before lines were split) counts as inspecting the whole line.
    /// </summary>
    public static int NextLeg(this Circuit circuit, IEnumerable<Mission> missions)
    {
        ArgumentNullException.ThrowIfNull(missions);
        var legs = circuit.Legs();
        var flown = missions.Where(m => m.CircuitId == circuit.Id && m.Status != MissionStatus.Aborted).ToList();
        DateTimeOffset? Last(int n) => flown.Where(m => m.Status == MissionStatus.Completed && (m.Leg is null || m.Leg == n))
            .Max(m => (DateTimeOffset?)(m.EndedAt ?? m.UpdatedAt));
        bool Busy(int n) => flown.Any(m => !Mission.Workflow.IsTerminal(m.Status) && m.Leg == n);
        var free = legs.Where(l => !Busy(l.Number)).ToList();
        return (free.Count > 0 ? free : legs)
            .OrderBy(l => Last(l.Number) ?? DateTimeOffset.MinValue).ThenBy(l => l.Number)
            .First().Number;
    }

    private static IReadOnlyList<CircuitLeg> Split(Circuit c)
    {
        var s = c.Structures;
        if (s.Count < 2) return [Make(c, 1, 1, 0, Math.Max(0, s.Count - 1), 0)];
        var at = new double[s.Count];
        for (var i = 1; i < s.Count; i++) at[i] = at[i - 1] + Geo.DistanceMeters(s[i - 1].Location, s[i].Location);
        var total = at[^1];

        // The fewest flights that each fit on a battery, cut at the structures nearest equal shares of the line.
        // (A single span longer than a flight, which real lines don't have, still gets a flight of its own.)
        for (var n = Math.Max(1, (int)Math.Ceiling(total / MaxLegMeters)); ; n++)
        {
            var cuts = new List<int> { 0 };
            for (var k = 1; k < n; k++)
            {
                var target = total * k / n;
                var i = Array.BinarySearch(at, target);
                if (i < 0) i = ~i;
                if (i > 0 && target - at[i - 1] < at[Math.Min(i, s.Count - 1)] - target) i--;
                i = Math.Clamp(i, cuts[^1] + 1, s.Count - 1 - (n - k));
                cuts.Add(i);
            }
            cuts.Add(s.Count - 1);
            var fits = cuts.Zip(cuts.Skip(1), (a, b) => at[b] - at[a] <= MaxLegMeters || b == a + 1).All(x => x);
            if (fits || n >= s.Count - 1)
                return [.. cuts.Zip(cuts.Skip(1)).Select((p, k) => Make(c, k + 1, cuts.Count - 1, p.First, p.Second, at[p.Second] - at[p.First]))];
        }
    }

    private static CircuitLeg Make(Circuit c, int number, int of, int first, int last, double length) =>
        new(number, of, first, last, c.Structures.Count > 0 ? c.Structures[first].Label : "", c.Structures.Count > 0 ? c.Structures[last].Label : "",
            last - first + 1, Math.Round(length), MinutesFor(length));
}
