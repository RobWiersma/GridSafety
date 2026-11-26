using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Tracing;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>
/// EventBridge rules (critical drone alerts, new findings, ended missions) → SQS → this function → SNS topics.
/// Supervisors choose in the console which of them send email (<see cref="AlertSettings"/>); the rest are dropped here.
/// SQS buffers bursts (one bad flight can raise many alerts) and adds retries and a DLQ.
/// Partial batch responses mean one bad message doesn't make the whole batch retry.
/// </summary>
public sealed class AlertNotifier(IOpsNotifier notifier, IdempotencyGuard idempotency, IAlertSettingsStore settingsStore)
{
    public AlertNotifier() : this(new AmazonDynamoDBClient(), Env.Required("TABLE_NAME")) { }

    private AlertNotifier(IAmazonDynamoDB db, string table)
        : this(SnsOpsNotifier.FromEnvironment(), new IdempotencyGuard(db, table), new DynamoAlertSettingsStore(db, table)) { }

    [Logging(LogEvent = false)]
    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task<SQSBatchResponse> HandleAsync(SQSEvent evt, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        var settings = await settingsStore.GetAsync(); // once per batch; if it fails, the whole batch is retried
        foreach (var message in evt.Records)
        {
            string? eventId = null;
            try
            {
                using var envelope = JsonDocument.Parse(message.Body);
                eventId = envelope.RootElement.GetProperty("id").GetString()!;
                var e = envelope.RootElement.GetProperty("detail").Deserialize<GridEvent>(Json.Options)!;
                if (Compose(e, settings) is not { } page) continue;

                if (!await idempotency.TryClaimAsync($"page:{eventId}", TimeSpan.FromDays(3)))
                {
                    Logger.LogInformation("Duplicate event {EventId} ignored", eventId);
                    continue;
                }
                try
                {
                    await Traced.Step("Page", e, () => notifier.NotifyAsync(page.Subject, page.Body, page.Severity, page.Type));
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

    /// <summary>Which kind of alert an event is, or null if it never sends email.</summary>
    public static AlertType? Classify(GridEvent e) => e.DetailType switch
    {
        EventNames.DroneAlertRaised when e.Level == nameof(AlertLevel.Critical) => AlertType.FlightAlert,
        EventNames.FindingCreated => AlertType.CriticalFinding,
        EventNames.MissionStatusChanged when e.NewStatus is nameof(MissionStatus.Completed) or nameof(MissionStatus.Aborted) => AlertType.MissionComplete,
        _ => null,
    };

    /// <summary>The email for an event, or null when the settings say not to send one.</summary>
    public static (string Subject, string Body, string Severity, AlertType Type)? Compose(GridEvent e, AlertSettings settings)
    {
        if (Classify(e) is not { } type || !settings.Allows(type, e.Severity)) return null;
        return type switch
        {
            AlertType.FlightAlert => (
                $"[GridSafety] FLIGHT ALERT {e.DroneId}: {e.NewStatus}",
                $"""
                 {e.Summary}

                 Mission: {e.MissionId}
                 Circuit: {e.CircuitId}
                 Time:    {e.OccurredAt:u}

                 The pilot in command has been alerted in the ops console. Confirm the aircraft is under control.
                 """,
                "critical", type),
            AlertType.CriticalFinding => (
                $"[GridSafety] Sev {e.Severity} finding: {e.Summary}",
                $"""
                 {e.Summary}

                 Mission:      {e.MissionId}
                 Triage due:   {e.TriageDueAt:u}
                 Severity:     {e.Severity}: {TriageSla.Describe(e.Severity ?? 0)}

                 Triage it in the GridSafety console before the deadline to avoid escalation.
                 """,
                e.Severity >= 5 ? "critical" : e.Severity >= 4 ? "high" : "info", type),
            _ => (
                $"[GridSafety] Mission {e.NewStatus?.ToLowerInvariant()}: {e.Summary}",
                $"""
                 {e.Summary}

                 Mission: {e.MissionId}
                 Circuit: {e.CircuitId}
                 Drone:   {e.DroneId}
                 Status:  {e.PreviousStatus} → {e.NewStatus}
                 Time:    {e.OccurredAt:u}

                 The inspection report is in the GridSafety console under Missions.
                 """,
                "info", type),
        };
    }
}
