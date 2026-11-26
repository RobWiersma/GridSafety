using System.Text.Json;
using Amazon.EventBridge;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Tracing;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>
/// Turns the table's change stream into domain events (an outbox without an outbox table):
/// writers only touch DynamoDB, so a change can never "commit but forget to publish".
/// The event source mapping filters to SK = META, so only missions and findings arrive here.
/// </summary>
public sealed class StreamPublisher(IEventPublisher publisher)
{
    public StreamPublisher() : this(new EventBridgePublisher(new AmazonEventBridgeClient(), Env.Required("EVENT_BUS_NAME"))) { }

    [Logging(LogEvent = false)]
    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task<StreamsEventResponse> HandleAsync(DynamoDBEvent evt, ILambdaContext context)
    {
        var response = new StreamsEventResponse { BatchItemFailures = [] };
        foreach (var record in evt.Records)
        {
            try
            {
                var e = ToEvent(
                    record.EventName?.ToString() ?? "",
                    Attr(record.Dynamodb?.NewImage, "EntityType") ?? Attr(record.Dynamodb?.OldImage, "EntityType"),
                    Attr(record.Dynamodb?.OldImage, "Doc"),
                    Attr(record.Dynamodb?.NewImage, "Doc"));
                if (e is null) continue;

                await Traced.Step("Publish", e, () => publisher.PublishAsync([e]));
                Logger.LogInformation("Published {DetailType} for {EntityId}", e.DetailType, e.EntityId);
            }
            catch (Exception ex)
            {
                // Streams are ordered per shard: report this record and stop so Lambda retries from here.
                Logger.LogError(ex, "Failed to publish stream record {Seq}", record.Dynamodb?.SequenceNumber);
                response.BatchItemFailures.Add(new StreamsEventResponse.BatchItemFailure { ItemIdentifier = record.Dynamodb?.SequenceNumber });
                break;
            }
        }
        return response;
    }

    /// <summary>Pure mapping from a stream record to a domain event (null when nothing worth publishing changed).</summary>
    public static GridEvent? ToEvent(string eventName, string? entityType, string? oldDoc, string? newDoc) => entityType switch
    {
        "Mission" => MissionEvent(eventName, Read<Mission>(oldDoc), Read<Mission>(newDoc)),
        "Finding" => FindingEvent(eventName, Read<Finding>(oldDoc), Read<Finding>(newDoc)),
        _ => null,
    };

    private static GridEvent? MissionEvent(string eventName, Mission? before, Mission? after)
    {
        if (after is null || eventName == "REMOVE") return null;
        var from = eventName == "INSERT" ? null : before?.Status;
        if (from == after.Status) return null;

        return new GridEvent
        {
            DetailType = EventNames.MissionStatusChanged,
            EntityType = "Mission",
            EntityId = after.Id,
            MissionId = after.Id,
            CircuitId = after.CircuitId,
            DroneId = after.DroneId,
            PreviousStatus = from?.ToString(),
            NewStatus = after.Status.ToString(),
            Summary = $"Mission {after.Id} ({after.DroneId} on {after.CircuitId}) is {after.Status}",
            OccurredAt = after.UpdatedAt,
        };
    }

    private static GridEvent? FindingEvent(string eventName, Finding? before, Finding? after)
    {
        if (after is null || eventName == "REMOVE") return null;
        var created = eventName == "INSERT";
        if (!created && before?.Status == after.Status) return null;

        return new GridEvent
        {
            DetailType = created ? EventNames.FindingCreated : EventNames.FindingStatusChanged,
            EntityType = "Finding",
            EntityId = after.Id,
            MissionId = after.MissionId,
            CircuitId = after.CircuitId,
            PreviousStatus = created ? null : before?.Status.ToString(),
            NewStatus = after.Status.ToString(),
            Severity = after.Severity,
            TriageDueAt = after.TriageDueAt,
            Summary = $"Sev {after.Severity} {Humanize(after.Type)} at {after.StructureLabel} ({after.CircuitId})",
            OccurredAt = after.UpdatedAt,
        };
    }

    internal static string Humanize(FindingType t) => t switch
    {
        FindingType.VegetationEncroachment => "vegetation encroachment",
        FindingType.DamagedInsulator => "damaged insulator",
        FindingType.CrossarmDamage => "crossarm damage",
        FindingType.ThermalHotspot => "thermal hotspot",
        FindingType.MissingHardware => "missing hardware",
        FindingType.ConductorDamage => "conductor damage",
        _ => t.ToString(),
    };

    private static T? Read<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json, Json.Options);

    private static string? Attr(Dictionary<string, DynamoDBEvent.AttributeValue>? image, string key) =>
        image is not null && image.TryGetValue(key, out var v) ? v.S : null;
}
