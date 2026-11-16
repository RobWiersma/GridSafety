using System.Text.Json;
using Amazon.EventBridge;
using Amazon.Lambda.Core;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Metrics;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>
/// Invoked by an AWS IoT Core topic rule for every MQTT message on <c>gridsafety/drones/+/+</c>:
/// <code>SELECT *, topic(3) AS droneId, topic(4) AS kind FROM 'gridsafety/drones/+/+'</code>
/// so the drone can't spoof its identity in the payload: droneId comes from the topic,
/// which the device's IoT policy restricts.
/// </summary>
public sealed class DroneIngestFunction(DroneIngestService ingest)
{
    public DroneIngestFunction() : this(new DroneIngestService(
        Repo.FromEnvironment(),
        StaticCircuitCatalog.Instance,
        new EventBridgePublisher(new AmazonEventBridgeClient(), Env.Required("EVENT_BUS_NAME")),
        TimeProvider.System))
    { }

    [Logging(LogEvent = false)]
    [Metrics(Namespace = "GridSafety")]
    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        var msg = await JsonSerializer.DeserializeAsync<DroneMessage>(input, Json.Options)
                  ?? throw new InvalidOperationException("Empty drone message");

        try
        {
            var result = await ingest.HandleAsync(msg);
            Metrics.AddMetric("TelemetryPoints", result.PointsStored, MetricUnit.Count);
            if (result.FindingId is not null) Metrics.AddMetric("FindingsReported", 1, MetricUnit.Count);
            if (result.AlertsRaised.Count > 0) Metrics.AddMetric("DroneAlerts", result.AlertsRaised.Count, MetricUnit.Count);
        }
        catch (NotFoundException ex)
        {
            // Bad references won't fix themselves on retry: log and drop instead of poisoning retries.
            Logger.LogWarning("Dropping {Kind} from {Drone}: {Reason}", msg.Kind, msg.DroneId, ex.Message);
            Metrics.AddMetric("MessagesRejected", 1, MetricUnit.Count);
        }
    }
}
