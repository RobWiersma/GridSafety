namespace GridSafety.Core;

public static class MessageKinds
{
    public const string Telemetry = "telemetry";
    public const string Finding = "finding";
    public const string Landed = "landed";
}

/// <summary>A drone's report of something it saw (produced by onboard/edge image models in real life).</summary>
public sealed record FindingReport(string StructureId, FindingType Type, int Severity, double Confidence, string? Description);

/// <summary>
/// Message from a drone. In AWS it arrives on MQTT topic <c>gridsafety/drones/{droneId}/{kind}</c>
/// and an IoT Core rule adds droneId/kind from the topic. Locally the simulator POSTs it to the API.
/// </summary>
public sealed record DroneMessage
{
    public required string Kind { get; init; }
    public required string DroneId { get; init; }
    public required string MissionId { get; init; }
    public List<TelemetryPoint>? Points { get; init; }
    public FindingReport? Finding { get; init; }
}

public sealed record IngestResult(int PointsStored, IReadOnlyList<DroneAlert> AlertsRaised, string? FindingId);

/// <summary>
/// The heart of the ingest path, shared by the IoT Lambda and the local dev endpoint.
/// Telemetry updates mission progress and the drone shadow and evaluates flight rules.
/// Findings become work orders with an SLA. "Landed" completes the mission.
/// </summary>
public sealed class DroneIngestService(IGridRepository repo, ICircuitCatalog circuits, IEventPublisher events, TimeProvider clock)
{
    /// <summary>A structure counts as inspected once the drone passes within this distance.</summary>
    public const double InspectionRadiusM = 45;

    public async Task<IngestResult> HandleAsync(DroneMessage msg, CancellationToken ct = default)
    {
        var mission = await repo.GetMissionAsync(msg.MissionId, ct) ?? throw new NotFoundException($"Mission {msg.MissionId}");
        if (!mission.DroneId.Equals(msg.DroneId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Drone {msg.DroneId} is not assigned to mission {mission.Id}.");
        var circuit = circuits.Find(mission.CircuitId) ?? throw new NotFoundException($"Circuit {mission.CircuitId}");

        return msg.Kind switch
        {
            MessageKinds.Telemetry => await TelemetryAsync(msg, circuit, ct),
            MessageKinds.Finding => await FindingAsync(msg, circuit, ct),
            MessageKinds.Landed => await LandedAsync(msg, ct),
            _ => throw new ArgumentException($"Unknown message kind '{msg.Kind}'"),
        };
    }

    private async Task<IngestResult> TelemetryAsync(DroneMessage msg, Circuit circuit, CancellationToken ct)
    {
        var points = (msg.Points ?? []).OrderBy(p => p.Timestamp).ToList();
        if (points.Count == 0) return new IngestResult(0, [], null);

        await repo.AppendTelemetryAsync(msg.MissionId, points, ct);

        var drone = await repo.GetDroneAsync(msg.DroneId, ct) ?? new DroneState { DroneId = msg.DroneId };
        var previouslyActive = drone.MissionId == msg.MissionId ? drone.ActiveAlerts.ToList() : [];
        var raised = new List<DroneAlert>();
        var active = previouslyActive;
        foreach (var p in points)
        {
            var current = FlightRules.Evaluate(p, circuit);
            raised.AddRange(FlightRules.NewlyRaised(active, current));
            active = current.Select(a => a.Kind).ToList();
        }

        var last = points[^1];
        if (last.Timestamp >= drone.LastSeen)
        {
            drone.Status = DroneStatus.Flying;
            drone.MissionId = msg.MissionId;
            drone.CircuitId = circuit.Id;
            drone.Position = last.Position;
            drone.AltitudeM = last.AltitudeM;
            drone.BatteryPct = last.BatteryPct;
            drone.SpeedMps = last.SpeedMps;
            drone.RssiDbm = last.RssiDbm;
            drone.LastSeen = last.Timestamp;
            drone.ActiveAlerts = active;
            await repo.SaveDroneAsync(drone, ct);
        }

        await UpdateMissionAsync(msg.MissionId, m =>
        {
            if (m.Status == MissionStatus.Planned) m.MoveTo(MissionStatus.InFlight, points[0].Timestamp, msg.DroneId, "First telemetry received");
            if (m.Status != MissionStatus.InFlight) return;

            foreach (var p in points)
            {
                if (m.LastPosition is { } prev) m.DistanceFlownMeters += Geo.DistanceMeters(prev, p.Position);
                m.LastPosition = p.Position;
                foreach (var s in circuit.Structures)
                {
                    if (!m.StructuresInspected.Contains(s.Id) && Geo.DistanceMeters(s.Location, p.Position) <= InspectionRadiusM)
                        m.StructuresInspected.Add(s.Id);
                }
            }
            m.BatteryPct = last.BatteryPct;
            m.RssiDbm = last.RssiDbm ?? m.RssiDbm;
            foreach (var a in raised) m.RecordAlert(a);
            m.UpdatedAt = clock.GetUtcNow();
        }, ct);

        if (raised.Count > 0)
        {
            await events.PublishAsync(raised.Select(a => new GridEvent
            {
                DetailType = EventNames.DroneAlertRaised,
                EntityType = "Drone",
                EntityId = msg.DroneId,
                DroneId = msg.DroneId,
                MissionId = msg.MissionId,
                CircuitId = circuit.Id,
                NewStatus = a.Kind.ToString(),
                Level = a.Level.ToString(),
                Summary = $"{msg.DroneId}: {a.Message}",
                OccurredAt = a.At,
            }).ToList(), ct);
        }

        return new IngestResult(points.Count, raised, null);
    }

    private async Task<IngestResult> FindingAsync(DroneMessage msg, Circuit circuit, CancellationToken ct)
    {
        var report = msg.Finding ?? throw new ArgumentException("Finding message without a finding.");
        var structure = circuit.Structures.FirstOrDefault(s => s.Id == report.StructureId)
                        ?? throw new NotFoundException($"Structure {report.StructureId} on {circuit.Id}");
        var severity = Math.Clamp(report.Severity, 1, 5);
        var now = clock.GetUtcNow();

        var finding = new Finding
        {
            Id = Ids.New(),
            MissionId = msg.MissionId,
            CircuitId = circuit.Id,
            StructureId = structure.Id,
            StructureLabel = structure.Label,
            Type = report.Type,
            Severity = severity,
            Confidence = Math.Clamp(report.Confidence, 0, 1),
            Location = structure.Location,
            Description = report.Description,
            CreatedAt = now,
            UpdatedAt = now,
            TriageDueAt = TriageSla.WindowFor(severity) is { } window ? now + window : null,
        };
        await repo.SaveFindingAsync(finding, ct);
        await UpdateMissionAsync(msg.MissionId, m => m.FindingCount++, ct);
        return new IngestResult(0, [], finding.Id);
    }

    private async Task<IngestResult> LandedAsync(DroneMessage msg, CancellationToken ct)
    {
        await UpdateMissionAsync(msg.MissionId, m =>
        {
            if (m.Status == MissionStatus.InFlight) m.MoveTo(MissionStatus.Completed, clock.GetUtcNow(), msg.DroneId, "Drone landed");
        }, ct);

        if (await repo.GetDroneAsync(msg.DroneId, ct) is { } drone)
        {
            drone.Status = DroneStatus.Idle;
            drone.SpeedMps = 0;
            drone.AltitudeM = 0;
            drone.ActiveAlerts = [];
            await repo.SaveDroneAsync(drone, ct);
        }
        return new IngestResult(0, [], null);
    }

    /// <summary>Read-modify-write with optimistic concurrency; retries when concurrent messages race.</summary>
    private async Task UpdateMissionAsync(string id, Action<Mission> change, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var m = await repo.GetMissionAsync(id, ct) ?? throw new NotFoundException($"Mission {id}");
            change(m);
            try
            {
                await repo.SaveMissionAsync(m, ct);
                return;
            }
            catch (ConcurrencyException) when (attempt < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt + Random.Shared.Next(20)), ct);
            }
        }
    }
}
