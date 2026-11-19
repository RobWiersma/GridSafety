using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Security", "CA5394:Do not use insecure randomness",
    Scope = "namespaceanddescendants", Target = "~N:GridSafety.Core.Simulation",
    Justification = "Simulated flights only need varied, optionally reproducible, values; nothing here is security-sensitive.")]

namespace GridSafety.Core.Simulation;

/// <summary>Where a simulated drone sends its messages: the local API, AWS IoT Core, or a test double.</summary>
public interface IDroneLink
{
    Task SendAsync(DroneMessage message, CancellationToken ct = default);
}

/// <summary>
/// Retries a send when the network fails (DNS not ready, connection reset), waiting between attempts.
/// A brand-new account's IoT Core endpoint can take a minute or two to resolve after it is first
/// described, so the very first demo flight would otherwise crash on takeoff.
/// </summary>
public sealed class RetryingDroneLink(IDroneLink inner, IReadOnlyList<TimeSpan> delays, TimeProvider clock, Action<Exception, int>? onRetry = null) : IDroneLink
{
    /// <summary>About 45 s in total: enough for new DNS to propagate, well inside the Lambda timeout.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays = [.. new[] { 1, 2, 4, 8, 15, 15 }.Select(s => TimeSpan.FromSeconds(s))];

    public async Task SendAsync(DroneMessage message, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await inner.SendAsync(message, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < delays.Count && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                onRetry?.Invoke(ex, attempt + 1);
                await Task.Delay(delays[attempt], clock, ct).ConfigureAwait(false);
            }
        }
    }

    public static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or TimeoutException
        || (ex.InnerException is { } inner && IsTransient(inner));
}

/// <summary>Pre-computed flight: follows the line with GPS jitter, drains battery, simulates link RSSI, and (optionally) has incidents.</summary>
public sealed record FlightPlan(IReadOnlyList<TelemetryPoint> Points, Structure SevereAt)
{
    public const double SpeedMps = 14;
    public const double SecondsPerTick = 2;

    /// <summary>How far the drone flies from the ground station before the crew moves it up the line.</summary>
    public const double RadioLegMeters = 2500;

    public static FlightPlan Build(Circuit c, Random rng, bool scriptedIncidents, DateTimeOffset start)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(rng);
        const double step = SpeedMps * SecondsPerTick;
        var path = c.Path;
        var samples = new List<GeoPoint>();
        for (var i = 0; i < path.Count - 1; i++)
        {
            var n = Math.Max(1, (int)(Geo.DistanceMeters(path[i], path[i + 1]) / step));
            for (var k = 0; k < n; k++) samples.Add(Geo.Lerp(path[i], path[i + 1], (double)k / n));
        }
        samples.Add(path[^1]);

        var severeAt = c.Structures[rng.Next(c.Structures.Count / 3, Math.Max(c.Structures.Count / 3 + 1, c.Structures.Count * 2 / 3))];
        var breachStart = scriptedIncidents ? rng.Next(samples.Count / 4, Math.Max(samples.Count / 4 + 1, samples.Count / 2)) : -1;
        var climbAt = scriptedIncidents && rng.NextDouble() < 0.6 ? rng.Next(samples.Count / 2, Math.Max(samples.Count / 2 + 1, samples.Count * 3 / 4)) : -1;
        // Radio interference (a substation, a ridge) knocks the link down for a few ticks.
        var fadeAt = scriptedIncidents && rng.NextDouble() < 0.5 ? rng.Next(samples.Count / 3, Math.Max(samples.Count / 3 + 1, samples.Count * 5 / 6)) : -1;
        // The ground crew leapfrogs its radio station along the line, so signal strength saw-tooths per leg.
        var station = samples[0];

        var points = new List<TelemetryPoint>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            // Drift sideways off the corridor for a few ticks (wind gust / pilot error), then recover.
            // "Sideways" is perpendicular to the line, whatever direction the line runs.
            var drift = breachStart >= 0 && i >= breachStart && i < breachStart + 8
                ? (c.CorridorMeters + 70) * Math.Sin(Math.PI * (i - breachStart) / 8)
                : 0;
            var (sideN, sideE) = drift == 0 ? (0d, 0d) : Perpendicular(samples, i);
            var pos = Geo.Offset(samples[i], rng.NextDouble() * 12 - 6 + drift * sideN, rng.NextDouble() * 12 - 6 + drift * sideE);
            var altitude = climbAt >= 0 && i >= climbAt && i < climbAt + 3 ? 126 + rng.NextDouble() * 4 : 58 + rng.NextDouble() * 10;
            var battery = 100 - 78.0 * i / Math.Max(1, samples.Count - 1);
            var fade = fadeAt >= 0 && i >= fadeAt && i < fadeAt + 6 ? 14 + 8 * Math.Sin(Math.PI * (i - fadeAt) / 6) : 0;
            if (Geo.DistanceMeters(station, samples[i]) > RadioLegMeters) station = samples[i];
            var rssi = Rssi(Geo.DistanceMeters(station, pos), altitude) - fade + rng.NextDouble() * 5 - 2.5;
            points.Add(new TelemetryPoint(start.AddSeconds(i * SecondsPerTick), pos.Lat, pos.Lon,
                Math.Round(altitude, 1), Math.Round(battery, 1), SpeedMps + rng.NextDouble() * 2 - 1, Math.Round(rssi, 1)));
        }
        return new FlightPlan(points, severeAt);
    }

    /// <summary>
    /// Log-distance path loss from the ground station: about -57 dBm at 100 m, -74 at 1 km,
    /// -86 at 5 km. Flying higher clears trees and terrain, so it helps a little.
    /// </summary>
    public static double Rssi(double distanceMeters, double altitudeM) =>
        Math.Min(-30, -40 - 17 * Math.Log10(Math.Max(distanceMeters, 10) / 10) + (altitudeM - 60) * 0.1);

    /// <summary>Unit vector (north, east) pointing left of the direction of travel at sample <paramref name="i"/>.</summary>
    private static (double North, double East) Perpendicular(List<GeoPoint> samples, int i)
    {
        var a = samples[Math.Max(0, Math.Min(i, samples.Count - 2))];
        var b = samples[Math.Max(1, Math.Min(i + 1, samples.Count - 1))];
        const double mPerDegree = 111_320;
        var north = (b.Lat - a.Lat) * mPerDegree;
        var east = (b.Lon - a.Lon) * mPerDegree * Math.Cos(a.Lat * Math.PI / 180);
        var len = Math.Sqrt(north * north + east * east);
        return len == 0 ? (0, 1) : (east / len, -north / len);
    }
}

/// <summary>What the drone's onboard model "sees" at a structure: usually nothing, sometimes a defect.</summary>
public static class FindingGenerator
{
    private static readonly (FindingType Type, string[] Notes)[] Catalog =
    [
        (FindingType.VegetationEncroachment, ["Oak limb within 4 ft of primary conductor", "Eucalyptus overhang above span", "Brush build-up at pole base"]),
        (FindingType.DamagedInsulator, ["Chipped porcelain bell on B phase", "Flashover tracking on pin insulator", "Polymer insulator shed damage"]),
        (FindingType.CrossarmDamage, ["Split crossarm at bolt hole", "Crossarm rot visible at brace", "Crossarm leaning ~10°"]),
        (FindingType.ThermalHotspot, ["Connector 38 °C above ambient", "Hot clamp on jumper (ΔT 25 °C)", "Transformer bushing warm"]),
        (FindingType.MissingHardware, ["Missing cotter key on clevis", "Bird guard missing", "Guy wire marker missing"]),
        (FindingType.ConductorDamage, ["Broken strands near splice", "Conductor clashing marks mid-span", "Bird-caging at dead-end"]),
    ];

    /// <summary>Chance that a structure has something wrong with it when the count isn't fixed.</summary>
    public const double DefectRate = 0.22;

    /// <param name="always">Report a finding for this structure (the flight was told how many to find).</param>
    public static FindingReport? Maybe(Structure s, Random rng, bool forceSevere, bool always = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(rng);
        if (!forceSevere && !always && rng.NextDouble() > DefectRate) return null;
        var (type, notes) = Catalog[rng.Next(Catalog.Length)];
        var roll = rng.NextDouble();
        var severity = forceSevere ? (rng.NextDouble() < 0.5 ? 5 : 4)
            : roll < 0.15 ? 1 : roll < 0.45 ? 2 : roll < 0.78 ? 3 : roll < 0.95 ? 4 : 5;
        return new FindingReport(s.Id, type, severity, Math.Round(0.62 + rng.NextDouble() * 0.36, 2), notes[rng.Next(notes.Length)]);
    }
}

/// <summary>Start a demo flight: which mission to fly. Sent from the API to the DemoFlight Lambda.</summary>
/// <param name="FindingCount">Exactly this many findings (see <see cref="SimulationSettings"/>), or null for random.</param>
public sealed record DemoFlightLaunch(string MissionId, string CircuitId, string DroneId, int? FindingCount = null);

/// <summary>
/// Flies a whole simulated mission through an <see cref="IDroneLink"/>: telemetry every tick, a finding
/// when a structure looks defective, then "landed". Used by the simulator CLI, the API's in-process demo
/// flight (local dev) and the DemoFlight Lambda (AWS, publishing through IoT Core).
/// </summary>
public static class DemoFlight
{
    /// <summary>The drone the console's "Demo flight" button uses.</summary>
    public const string DroneId = "DEMO-01";
    /// <summary>Flies the demo when no remote pilot is on duty.</summary>
    public const string Pilot = "Demo autopilot";

    /// <summary>
    /// The remote pilot (PIC) to fly a demo: on an on-duty flight crew, not flying another mission right now, and the one
    /// who has gone longest without flying, so the work rotates. Falls back to <see cref="Pilot"/> when nobody is free.
    /// </summary>
    public static string PickPilot(IEnumerable<FlightCrew> crews, IReadOnlyList<Mission> recent)
    {
        bool Flies(Mission m, CrewMember p) => m.Pilot.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase);
        return crews.Where(c => c.Status == CrewStatus.Available)
            .SelectMany(c => c.Pilots)
            .Where(p => !recent.Any(m => !Mission.Workflow.IsTerminal(m.Status) && Flies(m, p)))
            .OrderBy(p => recent.Where(m => Flies(m, p)).Max(m => (DateTimeOffset?)(m.StartedAt ?? m.CreatedAt)) ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()?.Name ?? Pilot;
    }

    /// <summary>A demo mission with no progress for this long is treated as abandoned (e.g. the flight crashed).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>Real-time delay between telemetry messages for on-demand demos (a 9 km line takes about 3 minutes).</summary>
    public static readonly TimeSpan DefaultTick = TimeSpan.FromMilliseconds(500);

    public static bool IsStale(Mission m, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(m);
        return !Mission.Workflow.IsTerminal(m.Status) && now - m.UpdatedAt > StaleAfter;
    }

    public static async Task FlyAsync(
        Circuit circuit, string droneId, string missionId, IDroneLink link, TimeSpan tick, Random rng, TimeProvider clock,
        bool scriptedIncidents = true, Func<bool>? keepFlying = null, int? findingCount = null,
        Action<int, int, TelemetryPoint>? onPoint = null, Action<Structure, FindingReport>? onFinding = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(clock);

        var plan = FlightPlan.Build(circuit, rng, scriptedIncidents, clock.GetUtcNow());
        var reported = new HashSet<string>();
        (HashSet<string> Ids, string? SevereId)? defective = findingCount is { } n ? PickDefective(circuit, plan, n, rng) : null;
        for (var i = 0; i < plan.Points.Count; i++)
        {
            if (keepFlying is not null && !keepFlying()) break; // e.g. Lambda about to time out: land early
            var p = plan.Points[i] with { Timestamp = clock.GetUtcNow() };
            await link.SendAsync(new DroneMessage { Kind = MessageKinds.Telemetry, DroneId = droneId, MissionId = missionId, Points = [p] }, ct);
            onPoint?.Invoke(i, plan.Points.Count, p);

            foreach (var s in circuit.Structures.Where(s => !reported.Contains(s.Id) && Geo.DistanceMeters(s.Location, p.Position) < 30))
            {
                reported.Add(s.Id);
                var finding = defective is null
                    ? FindingGenerator.Maybe(s, rng, forceSevere: s == plan.SevereAt)
                    : defective.Value.Ids.Contains(s.Id) ? FindingGenerator.Maybe(s, rng, forceSevere: s.Id == defective.Value.SevereId, always: true) : null;
                if (finding is not null)
                {
                    await link.SendAsync(new DroneMessage { Kind = MessageKinds.Finding, DroneId = droneId, MissionId = missionId, Finding = finding }, ct);
                    onFinding?.Invoke(s, finding);
                }
            }

            if (tick > TimeSpan.Zero) await Task.Delay(tick, clock, ct);
        }

        await link.SendAsync(new DroneMessage { Kind = MessageKinds.Landed, DroneId = droneId, MissionId = missionId }, ct);
    }

    /// <summary>
    /// Chooses which structures report a finding when the count is fixed: <paramref name="count"/> of the structures the
    /// flight passes (fewer if the line is short). When there is at least one, one of them is severe: the scripted
    /// severe structure if the drone passes it, otherwise another picked one.
    /// </summary>
    public static (HashSet<string> Ids, string? SevereId) PickDefective(Circuit circuit, FlightPlan plan, int count, Random rng)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rng);
        var passed = circuit.Structures.Where(s => plan.Points.Any(p => Geo.DistanceMeters(s.Location, p.Position) < 30)).ToList();
        var picked = new HashSet<string>();
        if (count <= 0 || passed.Count == 0) return (picked, null);
        if (passed.Contains(plan.SevereAt)) picked.Add(plan.SevereAt.Id);
        foreach (var s in passed.OrderBy(_ => rng.Next()))
        {
            if (picked.Count >= count) break;
            picked.Add(s.Id);
        }
        return (picked, picked.Contains(plan.SevereAt.Id) ? plan.SevereAt.Id : picked.First());
    }
}
