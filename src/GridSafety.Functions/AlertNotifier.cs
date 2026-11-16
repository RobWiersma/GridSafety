using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AWS.Lambda.Powertools.Logging;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>
/// EventBridge rules (critical drone alerts, severity 4+ findings) → SQS → this function → SNS ops topic.
/// SQS buffers bursts (one bad flight can raise many alerts) and adds retries and a DLQ.
/// Partial batch responses mean one bad message doesn't make the whole batch retry.
/// </summary>
public sealed class AlertNotifier(IOpsNotifier notifier, IdempotencyGuard idempotency)
{
    public AlertNotifier() : this(
        SnsOpsNotifier.FromEnvironment(),
        new IdempotencyGuard(new AmazonDynamoDBClient(), Env.Required("TABLE_NAME")))
    { }

    [Logging(LogEvent = false)]
    public async Task<SQSBatchResponse> HandleAsync(SQSEvent evt, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        foreach (var message in evt.Records)
        {
            string? eventId = null;
            try
            {
                using var envelope = JsonDocument.Parse(message.Body);
                eventId = envelope.RootElement.GetProperty("id").GetString()!;
                var e = envelope.RootElement.GetProperty("detail").Deserialize<GridEvent>(Json.Options)!;
                if (Compose(e) is not { } page) continue;

                if (!await idempotency.TryClaimAsync($"page:{eventId}", TimeSpan.FromDays(3)))
                {
                    Logger.LogInformation("Duplicate event {EventId} ignored", eventId);
                    continue;
                }
                try
                {
                    await notifier.NotifyAsync(page.Subject, page.Body, page.Severity);
                }
                catch
                {
                    await idempotency.ReleaseAsync($"page:{eventId}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to process message {MessageId} (event {EventId})", message.MessageId, eventId);
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
            }
        }
        return new SQSBatchResponse(failures);
    }

    public static (string Subject, string Body, string Severity)? Compose(GridEvent e) => e.DetailType switch
    {
        EventNames.DroneAlertRaised when e.Level == nameof(AlertLevel.Critical) => (
            $"[GridSafety] FLIGHT ALERT {e.DroneId}: {e.NewStatus}",
            $"""
             {e.Summary}

             Mission: {e.MissionId}
             Circuit: {e.CircuitId}
             Time:    {e.OccurredAt:u}

             The pilot in command has been alerted in the ops console. Confirm the aircraft is under control.
             """,
            "critical"),
        EventNames.FindingCreated when e.Severity >= 4 => (
            $"[GridSafety] Sev {e.Severity} finding: {e.Summary}",
            $"""
             {e.Summary}

             Mission:      {e.MissionId}
             Triage due:   {e.TriageDueAt:u}
             Severity:     {e.Severity}: {TriageSla.Describe(e.Severity ?? 0)}

             Triage it in the GridSafety console before the deadline to avoid escalation.
             """,
            e.Severity >= 5 ? "critical" : "high"),
        _ => null,
    };
}
