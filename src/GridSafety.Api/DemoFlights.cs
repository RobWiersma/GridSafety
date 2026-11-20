using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using GridSafety.Core;
using GridSafety.Core.Simulation;

namespace GridSafety.Api;

public sealed record DemoFlightRequest(string? CircuitId);

/// <summary>Starts a simulated flight for a mission the API has just planned. Runs on demand only; nothing is scheduled.</summary>
public interface IDemoFlightLauncher
{
    bool Enabled { get; }
    Task LaunchAsync(DemoFlightLaunch launch, CancellationToken ct);
}

/// <summary>AWS: invokes the DemoFlight Lambda asynchronously; it publishes the flight through IoT Core.</summary>
public sealed class LambdaDemoFlightLauncher(IAmazonLambda lambda, string functionName) : IDemoFlightLauncher
{
    public bool Enabled => true;

    public async Task LaunchAsync(DemoFlightLaunch launch, CancellationToken ct)
    {
        var res = await lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = functionName,
            InvocationType = InvocationType.Event, // fire and forget: the flight outlives this HTTP request
            Payload = JsonSerializer.Serialize(launch, Json.Options),
        }, ct);
        if (res.StatusCode != 202) throw new InvalidOperationException($"DemoFlight Lambda returned {res.StatusCode}");
    }
}

/// <summary>
/// Local dev: flies the drone in a background task straight into the ingest service, the same path
/// <c>/api/ingest</c> uses, so the button works without an AWS account.
/// </summary>
public sealed class InProcessDemoFlightLauncher(
    DroneIngestService ingest, ICircuitCatalog circuits, TimeProvider clock, IHostApplicationLifetime lifetime,
    IConfiguration config, ILogger<InProcessDemoFlightLauncher> log) : IDemoFlightLauncher
{
    public bool Enabled => true;

    public Task LaunchAsync(DemoFlightLaunch launch, CancellationToken ct)
    {
        var line = circuits.Find(launch.CircuitId) ?? throw new NotFoundException($"Circuit {launch.CircuitId}");
        var tick = TimeSpan.FromMilliseconds(config.GetValue("GridSafety:DemoTickMs", DemoFlight.DefaultTick.TotalMilliseconds));
        var stopping = lifetime.ApplicationStopping;

        _ = Task.Run(async () =>
        {
            try
            {
                // An outing's flights go one after another, with a battery swap between.
                for (DemoFlightLaunch? flight = launch; flight is not null; flight = flight.Next)
                {
                    if (flight != launch) await Task.Delay(tick * 10 < DemoFlightLaunch.BatterySwap ? tick * 10 : DemoFlightLaunch.BatterySwap, clock, stopping);
                    await DemoFlight.FlyAsync(line.Stretch(flight.Leg), flight.DroneId, flight.MissionId, new IngestLink(ingest), tick, new Random(), clock,
                        findingCount: flight.FindingCount, ct: stopping);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
#pragma warning disable CA1031 // A background flight must never take the API down; log and let the mission go stale.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.LogError(ex, "Demo flight {MissionId} failed", launch.MissionId);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private sealed class IngestLink(DroneIngestService ingest) : IDroneLink
    {
        public Task SendAsync(DroneMessage message, CancellationToken ct = default) => ingest.HandleAsync(message, ct);
    }
}

public sealed class DisabledDemoFlightLauncher : IDemoFlightLauncher
{
    public static readonly DisabledDemoFlightLauncher Instance = new();

    public bool Enabled => false;

    public Task LaunchAsync(DemoFlightLaunch launch, CancellationToken ct) =>
        throw new InvalidOperationException("Demo flights are not configured.");
}
