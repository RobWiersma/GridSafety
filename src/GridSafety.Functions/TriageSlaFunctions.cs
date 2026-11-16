using System.Globalization;
using System.Text.Json;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Metrics;
using GridSafety.Core;

namespace GridSafety.Functions;

public sealed record EscalationPayload(string FindingId);

public sealed record SlaSettings(string GroupName, string EscalationFunctionArn, string SchedulerRoleArn);

/// <summary>
/// When a severity 3+ finding is created, book a one-time EventBridge Scheduler job at its triage deadline.
/// When anyone triages or dismisses it first, cancel the job. No polling and no table scans.
/// </summary>
public sealed class SlaScheduler(IAmazonScheduler scheduler, SlaSettings settings)
{
    public SlaScheduler() : this(new AmazonSchedulerClient(), new SlaSettings(
        Env.Required("SCHEDULE_GROUP"), Env.Required("ESCALATION_FUNCTION_ARN"), Env.Required("SCHEDULER_ROLE_ARN")))
    { }

    [Logging(LogEvent = false)]
    public async Task HandleAsync(CloudWatchEvent<JsonElement> evt, ILambdaContext context)
    {
        var e = evt.Detail.Deserialize<GridEvent>(Json.Options) ?? throw new InvalidOperationException("Empty event detail");
        var name = ScheduleName(e.EntityId);

        if (ShouldSchedule(e))
        {
            await UpsertAsync(name, e.TriageDueAt!.Value, new EscalationPayload(e.EntityId));
            Logger.LogInformation("SLA timer for {FindingId} due {Due}", e.EntityId, e.TriageDueAt);
        }
        else if (e.DetailType == EventNames.FindingStatusChanged)
        {
            try
            {
                await scheduler.DeleteScheduleAsync(new DeleteScheduleRequest { Name = name, GroupName = settings.GroupName });
                Logger.LogInformation("SLA timer for {FindingId} cancelled ({Status})", e.EntityId, e.NewStatus);
            }
            catch (ResourceNotFoundException)
            {
                // Already fired or never scheduled.
            }
        }
    }

    public static bool ShouldSchedule(GridEvent e) =>
        e.DetailType == EventNames.FindingCreated && e.TriageDueAt is not null && e.NewStatus == nameof(FindingStatus.New);

    public static string ScheduleName(string findingId) => $"sla-{findingId}";

    public static string AtExpression(DateTimeOffset when) =>
        $"at({when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)})";

    private async Task UpsertAsync(string name, DateTimeOffset due, EscalationPayload payload)
    {
        var target = new Target
        {
            Arn = settings.EscalationFunctionArn,
            RoleArn = settings.SchedulerRoleArn,
            Input = JsonSerializer.Serialize(payload, Json.Options),
            RetryPolicy = new RetryPolicy { MaximumRetryAttempts = 3 },
        };
        try
        {
            await scheduler.CreateScheduleAsync(new CreateScheduleRequest
            {
                Name = name,
                GroupName = settings.GroupName,
                ScheduleExpression = AtExpression(due),
                ScheduleExpressionTimezone = "UTC",
                FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                ActionAfterCompletion = ActionAfterCompletion.DELETE,
                Target = target,
            });
        }
        catch (ConflictException)
        {
            // Redelivered event: the timer already exists, and the deadline was fixed at creation.
        }
    }
}

/// <summary>Fires at the triage deadline. If the finding is still untouched, flag it and page the on-call supervisor.</summary>
public sealed class SlaEscalation(IGridRepository repo, IOpsNotifier notifier, TimeProvider clock)
{
    public SlaEscalation() : this(Repo.FromEnvironment(), SnsOpsNotifier.FromEnvironment(), TimeProvider.System) { }

    [Logging(LogEvent = true)]
    [Metrics(Namespace = "GridSafety")]
    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        var payload = await JsonSerializer.DeserializeAsync<EscalationPayload>(input, Json.Options)
                      ?? throw new InvalidOperationException("Empty escalation payload");

        var f = await repo.GetFindingAsync(payload.FindingId);
        if (f is null || f.Status != FindingStatus.New || f.Escalated)
        {
            Logger.LogInformation("No escalation needed for {FindingId}", payload.FindingId);
            return;
        }

        f.Escalated = true;
        f.UpdatedAt = clock.GetUtcNow();
        try
        {
            await repo.SaveFindingAsync(f); // conditional write: if someone triaged just now, we lose the race and don't page
        }
        catch (ConcurrencyException)
        {
            Logger.LogInformation("{FindingId} changed concurrently; skipping escalation", f.Id);
            return;
        }

        var overdueBy = clock.GetUtcNow() - f.TriageDueAt!.Value;
        await notifier.NotifyAsync(
            $"[GridSafety] SLA breach: Sev {f.Severity} {StreamPublisher.Humanize(f.Type)} at {f.StructureLabel}",
            $"""
             Finding {f.Id} has not been triaged within its SLA.

             Circuit:    {f.CircuitId}
             Structure:  {f.StructureLabel}
             Type:       {StreamPublisher.Humanize(f.Type)}
             Severity:   {f.Severity}: {TriageSla.Describe(f.Severity)}
             Reported:   {f.CreatedAt:u}
             Due:        {f.TriageDueAt:u} ({overdueBy.TotalMinutes:0} min ago)
             Location:   https://maps.google.com/?q={f.Location.Lat:0.00000},{f.Location.Lon:0.00000}
             """,
            "critical");
        Metrics.AddMetric("SlaEscalations", 1, MetricUnit.Count);
    }
}
