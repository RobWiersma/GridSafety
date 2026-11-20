namespace GridSafety.Core;

/// <param name="RssiDbm">Control-link signal strength at the drone, in dBm (closer to 0 is stronger). Null for drones that don't report it.</param>
public sealed record TelemetryPoint(DateTimeOffset Timestamp, double Lat, double Lon, double AltitudeM, double BatteryPct, double SpeedMps, double? RssiDbm = null)
{
    public GeoPoint Position => new(Lat, Lon);
}

public enum AlertKind { LowBattery, CriticalBattery, CorridorBreach, AltitudeCeiling, WeakSignal, LinkCritical }

public enum AlertLevel { Warning, Critical }

/// <param name="AcknowledgedAt">When someone in ops acknowledged the alert; null while it still needs attention.</param>
public sealed record DroneAlert(AlertKind Kind, AlertLevel Level, string Message, DateTimeOffset At, GeoPoint Position,
    DateTimeOffset? AcknowledgedAt = null, string? AcknowledgedBy = null);

public enum DroneStatus { Idle, Flying }

/// <summary>Latest known state of a drone ("device shadow" for the ops map).</summary>
public sealed class DroneState
{
    public required string DroneId { get; init; }
    public DroneStatus Status { get; set; }
    public string? MissionId { get; set; }
    public string? CircuitId { get; set; }
    public GeoPoint Position { get; set; }
    public double AltitudeM { get; set; }
    public double BatteryPct { get; set; }
    public double SpeedMps { get; set; }
    public double? RssiDbm { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public List<AlertKind> ActiveAlerts { get; set; } = [];

    /// <summary>This drone on the ground: idle, stopped, no alerts. Everything else (where and when it was last seen) is kept.</summary>
    public DroneState Landed() => new()
    {
        DroneId = DroneId, Status = DroneStatus.Idle, MissionId = MissionId, CircuitId = CircuitId, Position = Position,
        AltitudeM = 0, BatteryPct = BatteryPct, SpeedMps = 0, RssiDbm = RssiDbm, LastSeen = LastSeen, ActiveAlerts = [],
    };

    /// <summary>
    /// The drones as they really are: one still marked flying whose mission has ended (or is too old to be in
    /// <paramref name="missions"/>) is shown landed. Covers a "landed" message whose update was lost to a telemetry
    /// batch saved at the same moment.
    /// </summary>
    public static IReadOnlyList<DroneState> Settled(IEnumerable<DroneState> drones, IEnumerable<Mission> missions)
    {
        var flying = missions.Where(m => !Mission.Workflow.IsTerminal(m.Status)).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return [.. drones.Select(d => d.Status == DroneStatus.Flying && (d.MissionId is null || !flying.Contains(d.MissionId)) ? d.Landed() : d)];
    }
}

/// <summary>Flight-safety rules evaluated on every telemetry point.</summary>
public static class FlightRules
{
    public const double LowBatteryPct = 30;
    public const double CriticalBatteryPct = 15;

    /// <summary>FAA Part 107 ceiling: 400 ft AGL ≈ 122 m. We alert slightly below it.</summary>
    public const double AltitudeCeilingM = 120;

    /// <summary>Control-link thresholds. Below about -95 dBm a typical 2.4 GHz link starts dropping packets.</summary>
    public const double WeakSignalDbm = -85;
    public const double LinkCriticalDbm = -95;

    public static IReadOnlyList<DroneAlert> Evaluate(TelemetryPoint p, Circuit circuit)
    {
        var alerts = new List<DroneAlert>();

        if (p.BatteryPct <= CriticalBatteryPct)
            alerts.Add(new(AlertKind.CriticalBattery, AlertLevel.Critical, $"Battery at {p.BatteryPct:0}%: return to launch now", p.Timestamp, p.Position));
        else if (p.BatteryPct <= LowBatteryPct)
            alerts.Add(new(AlertKind.LowBattery, AlertLevel.Warning, $"Battery at {p.BatteryPct:0}%: plan return", p.Timestamp, p.Position));

        if (p.RssiDbm is { } rssi)
        {
            if (rssi <= LinkCriticalDbm)
                alerts.Add(new(AlertKind.LinkCritical, AlertLevel.Critical, $"Control link at {rssi:0} dBm: link loss imminent, return to launch", p.Timestamp, p.Position));
            else if (rssi <= WeakSignalDbm)
                alerts.Add(new(AlertKind.WeakSignal, AlertLevel.Warning, $"Weak control link: {rssi:0} dBm", p.Timestamp, p.Position));
        }

        var offLine = Geo.DistanceToPolylineMeters(p.Position, circuit.Path);
        if (offLine > circuit.CorridorMeters)
            alerts.Add(new(AlertKind.CorridorBreach, AlertLevel.Critical,
                $"{offLine:0} m outside the {circuit.CorridorMeters:0} m corridor of {circuit.Name}", p.Timestamp, p.Position));

        if (p.AltitudeM > AltitudeCeilingM)
            alerts.Add(new(AlertKind.AltitudeCeiling, AlertLevel.Critical, $"Altitude {p.AltitudeM:0} m exceeds {AltitudeCeilingM:0} m ceiling", p.Timestamp, p.Position));

        return alerts;
    }

    /// <summary>Only alerts that just started fire notifications; a sustained condition doesn't re-page.</summary>
    public static IReadOnlyList<DroneAlert> NewlyRaised(IReadOnlyCollection<AlertKind> previouslyActive, IReadOnlyList<DroneAlert> current) =>
        current.Where(a => !previouslyActive.Contains(a.Kind)
                           && !(a.Kind == AlertKind.LowBattery && previouslyActive.Contains(AlertKind.CriticalBattery))
                           && !(a.Kind == AlertKind.WeakSignal && previouslyActive.Contains(AlertKind.LinkCritical)))
               .ToList();
}
