using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>"ready" with the framed SVG, "pending" while the photo is being fetched, or "unavailable".</summary>
public sealed record AerialView(string Status, string? Svg = null);

/// <summary>Starts a background fetch of one structure's aerial photo. The caller has already claimed it in the store.</summary>
public interface IAerialLauncher
{
    bool Enabled { get; }
    Task LaunchAsync(AerialRequest request, CancellationToken ct);
}

/// <summary>AWS: invokes the AerialImagery Lambda asynchronously (a fetch takes 15–30 s, longer than an API call may).</summary>
public sealed class LambdaAerialLauncher(IAmazonLambda lambda, string functionName) : IAerialLauncher
{
    public bool Enabled => true;

    public async Task LaunchAsync(AerialRequest request, CancellationToken ct)
    {
        var res = await lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = functionName,
            InvocationType = InvocationType.Event,
            Payload = JsonSerializer.Serialize(request, Json.Options),
        }, ct);
        if (res.StatusCode != 202) throw new InvalidOperationException($"AerialImagery Lambda returned {res.StatusCode}");
    }
}

/// <summary>Local dev and tests: fetch in a background task in this process.</summary>
public sealed class InProcessAerialLauncher(
    IAerialSource source, IAerialStore store, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<InProcessAerialLauncher> log) : IAerialLauncher
{
    public bool Enabled => true;

    public Task LaunchAsync(AerialRequest request, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var status = await AerialPolicy.FetchAsync(request, source, store, clock, lifetime.ApplicationStopping);
                log.LogInformation("Aerial photo for {Structure}: {Status}", request.StructureId, status);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
#pragma warning disable CA1031 // A background fetch must never take the API down; the claim simply expires.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.LogError(ex, "Aerial photo for {Structure} failed", request.StructureId);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }
}

public sealed class DisabledAerialLauncher : IAerialLauncher
{
    public static readonly DisabledAerialLauncher Instance = new();
    public bool Enabled => false;
    public Task LaunchAsync(AerialRequest request, CancellationToken ct) => Task.CompletedTask;
}

public static partial class Endpoints
{
    /// <summary>
    /// The finding's capture over real aerial imagery of its structure. The first request for a structure starts a
    /// background fetch and answers "pending"; poll until "ready". <paramref name="fetch"/>=false only reads the cache
    /// (thumbnails use it so a page of findings doesn't start a fetch for each).
    /// </summary>
    private static async Task<Results<Ok<AerialView>, NotFound>> GetFindingAerial(
        string id, bool? fetch, IGridRepository repo, IAerialStore store, IAerialLauncher launcher, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var f = await repo.GetFindingAsync(id, ct);
        if (f is null) return TypedResults.NotFound();

        var now = clock.GetUtcNow();
        var img = await store.GetAsync(f.StructureId, ct);
        switch (AerialPolicy.Effective(img, now))
        {
            case AerialStatus.Ready: return TypedResults.Ok(new AerialView("ready", FindingImage.AerialSvg(f, img.Jpeg!)));
            case AerialStatus.Pending: return TypedResults.Ok(new AerialView("pending"));
            case AerialStatus.Failed: return TypedResults.Ok(new AerialView("unavailable"));
        }

        if (fetch == false || !launcher.Enabled) return TypedResults.Ok(new AerialView("unavailable"));
        if (!await store.TryClaimAsync(f.StructureId, now, ct)) return TypedResults.Ok(new AerialView("pending")); // someone else just started it
        try
        {
            await launcher.LaunchAsync(new AerialRequest(f.StructureId, f.Location), ct);
        }
#pragma warning disable CA1031 // Can't start the fetch: say so, and let the failure marker hold off retries for a while.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logs.CreateLogger("Aerial").LogError(ex, "Could not start the aerial fetch for {Structure}", f.StructureId);
            await store.MarkFailedAsync(f.StructureId, now, CancellationToken.None);
            return TypedResults.Ok(new AerialView("unavailable"));
        }
        return TypedResults.Ok(new AerialView("pending"));
    }
}
