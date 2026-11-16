namespace GridSafety.Core;

public sealed record TelemetryPoint(DateTimeOffset Timestamp, double Lat, double Lon, double AltitudeM, double BatteryPct, double SpeedMps)
{
    public GeoPoint Position => new(Lat, Lon);
}

public enum AlertKind { LowBattery, CriticalBattery, CorridorBreach, AltitudeCeiling }

public enum AlertLevel { Warning, Critical }

public sealed record DroneAlert(AlertKind Kind, AlertLevel Level, string Message, DateTimeOffset At, GeoPoint Position);

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
    public DateTimeOffset LastSeen { get; set; }
    public List<AlertKind> ActiveAlerts { get; set; } = [];
}

/// <summary>Flight-safety rules evaluated on every telemetry point.</summary>
public static class FlightRules
{
    public const double LowBatteryPct = 30;
    public const double CriticalBatteryPct = 15;

    /// <summary>FAA Part 107 ceiling: 400 ft AGL ≈ 122 m. We alert slightly below it.</summary>
    public const double AltitudeCeilingM = 120;

    public static IReadOnlyList<DroneAlert> Evaluate(TelemetryPoint p, Circuit circuit)
    {
        var alerts = new List<DroneAlert>();

        if (p.BatteryPct <= CriticalBatteryPct)
            alerts.Add(new(AlertKind.CriticalBattery, AlertLevel.Critical, $"Battery at {p.BatteryPct:0}%: return to launch now", p.Timestamp, p.Position));
        else if (p.BatteryPct <= LowBatteryPct)
            alerts.Add(new(AlertKind.LowBattery, AlertLevel.Warning, $"Battery at {p.BatteryPct:0}%: plan return", p.Timestamp, p.Position));

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
                           && !(a.Kind == AlertKind.LowBattery && previouslyActive.Contains(AlertKind.CriticalBattery)))
               .ToList();
}
