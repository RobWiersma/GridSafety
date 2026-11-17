using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>Where one recipient's email subscription stands: Confirmed, Pending (they haven't clicked the link yet), NotSubscribed, Local or Unknown.</summary>
public sealed record RecipientStatus(string Email, string Status);

/// <param name="EmailEnabled">False locally: settings are saved but no email is sent.</param>
/// <param name="Warning">Set when the settings saved but a recipient change could not be applied.</param>
public sealed record AlertSettingsView(AlertSettings Settings, IReadOnlyList<RecipientStatus> Recipients, bool EmailEnabled, string? Warning = null);

/// <summary>Turns the recipient list into email subscriptions.</summary>
public interface IAlertRecipients
{
    bool EmailEnabled { get; }
    Task<IReadOnlyList<RecipientStatus>> StatusAsync(IReadOnlyList<string> emails, CancellationToken ct = default);

    /// <summary>Subscribes new addresses and unsubscribes confirmed ones that were removed.</summary>
    Task SyncAsync(IReadOnlyList<string> emails, CancellationToken ct = default);
}

/// <summary>Local development: nothing is emailed.</summary>
public sealed class LocalAlertRecipients : IAlertRecipients
{
    public bool EmailEnabled => false;

    public Task<IReadOnlyList<RecipientStatus>> StatusAsync(IReadOnlyList<string> emails, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RecipientStatus>>(emails.Select(e => new RecipientStatus(e, "Local")).ToList());

    public Task SyncAsync(IReadOnlyList<string> emails, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Recipients are email subscriptions on an SNS topic that only this API manages (the deploy-time ops address
/// lives on the ops topic, so it can't be removed here). SNS emails each new address a confirmation link;
/// nothing needs verifying in the AWS account, unlike SES in its sandbox.
/// </summary>
public sealed class SnsAlertRecipients(IAmazonSimpleNotificationService sns, string topicArn) : IAlertRecipients
{
    private const string Pending = "PendingConfirmation";

    public bool EmailEnabled => true;

    public async Task<IReadOnlyList<RecipientStatus>> StatusAsync(IReadOnlyList<string> emails, CancellationToken ct = default)
    {
        var subs = await ListAsync(ct);
        return emails.Select(e => new RecipientStatus(e, subs.FirstOrDefault(s => Same(s.Endpoint, e)) switch
        {
            null => "NotSubscribed",
            { SubscriptionArn: Pending } => "Pending",
            _ => "Confirmed",
        })).ToList();
    }

    public async Task SyncAsync(IReadOnlyList<string> emails, CancellationToken ct = default)
    {
        var subs = await ListAsync(ct);
        foreach (var email in emails.Where(e => !subs.Any(s => Same(s.Endpoint, e))))
            await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = email }, ct);
        // A pending subscription can't be deleted; SNS drops it if the link is never clicked.
        foreach (var sub in subs.Where(s => s.SubscriptionArn != Pending && !emails.Any(e => Same(s.Endpoint, e))))
            await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub.SubscriptionArn }, ct);
    }

    private async Task<List<Subscription>> ListAsync(CancellationToken ct)
    {
        var all = new List<Subscription>();
        string? next = null;
        do
        {
            var page = await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = next }, ct);
            all.AddRange((page.Subscriptions ?? []).Where(s => s.Protocol == "email"));
            next = page.NextToken;
        } while (next is { Length: > 0 });
        return all;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Email alert settings: any signed-in user can see them, supervisors change them. Not on the guest API (it lists addresses).</summary>
public static partial class Endpoints
{
    private static void MapAlertSettings(RouteGroupBuilder secured)
    {
        secured.MapGet("/alert-settings", GetAlertSettings);
        secured.MapPut("/alert-settings", PutAlertSettings);
    }

    private static async Task<Ok<AlertSettingsView>> GetAlertSettings(IAlertSettingsStore store, IAlertRecipients recipients, ILogger<AlertSettingsView> log, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        return TypedResults.Ok(new AlertSettingsView(settings, await RecipientStatusAsync(settings, recipients, log, ct), recipients.EmailEnabled));
    }

    private static async Task<IResult> PutAlertSettings(
        AlertSettings body, HttpContext ctx, IAlertSettingsStore store, IAlertRecipients recipients, TimeProvider clock, ILogger<AlertSettingsView> log, CancellationToken ct)
    {
        var user = User(ctx);
        if (!user.IsSupervisor)
            return TypedResults.Problem("Only supervisors can change email alerts.", statusCode: StatusCodes.Status403Forbidden);

        body.Recipients = AlertSettings.NormalizeRecipients(body.Recipients);
        if (AlertSettings.Validate(body) is { Count: > 0 } errors) return TypedResults.ValidationProblem(errors);
        body.UpdatedBy = user.DisplayName;
        body.UpdatedAt = clock.GetUtcNow();
        try
        {
            await store.SaveAsync(body, ct);
        }
        catch (ConcurrencyException)
        {
            return TypedResults.Conflict("Someone else changed the alert settings. Reload to see their changes.");
        }

        string? warning = null;
        try
        {
            await recipients.SyncAsync(body.Recipients, ct);
        }
        catch (Exception ex) when (ex is AmazonSimpleNotificationServiceException or HttpRequestException)
        {
            log.LogError(ex, "Alert settings saved but recipient subscriptions failed");
            warning = "Saved, but the recipient list couldn't be applied to email subscriptions. Save again to retry.";
        }
        return TypedResults.Ok(new AlertSettingsView(body, await RecipientStatusAsync(body, recipients, log, ct), recipients.EmailEnabled, warning));
    }

    private static async Task<IReadOnlyList<RecipientStatus>> RecipientStatusAsync(AlertSettings s, IAlertRecipients recipients, ILogger log, CancellationToken ct)
    {
        try
        {
            return await recipients.StatusAsync(s.Recipients, ct);
        }
        catch (Exception ex) when (ex is AmazonSimpleNotificationServiceException or HttpRequestException)
        {
            log.LogWarning(ex, "Couldn't read alert subscriptions");
            return s.Recipients.Select(e => new RecipientStatus(e, "Unknown")).ToList();
        }
    }
}
