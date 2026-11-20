using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.Lambda;
using Amazon.Lambda.Core;
using Amazon.Lambda.Model;
using AWS.Lambda.Powertools.Logging;
using GridSafety.Core;
using GridSafety.Core.Simulation;

namespace GridSafety.Functions;

/// <summary>
/// On-demand demo flight, started by the console's "Demo flight" button (the API invokes this asynchronously).
/// Publishes a simulated mission to <c>gridsafety/drones/{drone}/{kind}</c> on IoT Core, exactly like a real
/// drone, so the whole pipeline runs: topic rule, ingest, alerts, findings, SLA timers, paging and the report.
/// There is no schedule: it only runs when someone presses the button.
/// </summary>
public sealed class DemoFlightFunction
{
    private static readonly Lazy<Task<IAmazonIotData>> DataPlane = new(async () =>
    {
        using var iot = new AmazonIoTClient();
        var endpoint = await iot.DescribeEndpointAsync(new DescribeEndpointRequest { EndpointType = "iot:Data-ATS" });
        return new AmazonIotDataClient($"https://{endpoint.EndpointAddress}");
    });

    /// <summary>Stop sending telemetry this long before the Lambda timeout so the drone still lands.</summary>
    private static readonly TimeSpan LandingReserve = TimeSpan.FromSeconds(15);

    [Logging(LogEvent = true)]
#pragma warning disable CA1822 // The Lambda runtime invokes handlers on an instance.
    public async Task HandleAsync(System.IO.Stream input, ILambdaContext context)
#pragma warning restore CA1822
    {
        ArgumentNullException.ThrowIfNull(context);
        var launch = await JsonSerializer.DeserializeAsync<DemoFlightLaunch>(input, Json.Options)
                     ?? throw new InvalidOperationException("Empty demo flight request");
        var circuit = (StaticCircuitCatalog.Instance.Find(launch.CircuitId) ?? throw new NotFoundException($"Circuit {launch.CircuitId}")).Stretch(launch.Leg);
        var tick = int.TryParse(Env.Optional("DEMO_TICK_MS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : DemoFlight.DefaultTick;

        Logger.LogInformation("Demo flight {MissionId}: {DroneId} over {Circuit}", launch.MissionId, launch.DroneId, circuit.Name);
        var link = new IotCoreDroneLink(await DataPlane.Value);
        var retrying = new RetryingDroneLink(link, RetryingDroneLink.DefaultDelays, TimeProvider.System,
            (ex, attempt) => Logger.LogWarning("IoT publish failed, retry {Attempt}: {Error}", attempt, ex.Message));
        await DemoFlight.FlyAsync(circuit, launch.DroneId, launch.MissionId, retrying, tick, new Random(), TimeProvider.System,
            keepFlying: () => context.RemainingTime > LandingReserve, findingCount: launch.FindingCount);
        Logger.LogInformation("Demo flight {MissionId} landed after {Messages} messages", launch.MissionId, link.Sent);

        // The next flight of an outing goes up in a fresh invocation of this function, after the battery swap, so a whole
        // outing never runs into one invocation's time limit.
        if (launch.Next is { } next)
        {
            await Task.Delay(DemoFlightLaunch.BatterySwap);
            using var lambda = new AmazonLambdaClient();
            await lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = context.InvokedFunctionArn,
                InvocationType = InvocationType.Event,
                Payload = JsonSerializer.Serialize(next, Json.Options),
            });
            Logger.LogInformation("Outing continues: flight {MissionId} is next", next.MissionId);
        }
    }
}

/// <summary>Publishes drone messages over the IoT Core data plane (HTTPS, SigV4, QoS 1).</summary>
internal sealed class IotCoreDroneLink(IAmazonIotData data) : IDroneLink
{
    public int Sent { get; private set; }

    public async Task SendAsync(DroneMessage message, CancellationToken ct = default)
    {
        await data.PublishAsync(new PublishRequest
        {
            Topic = $"gridsafety/drones/{message.DroneId}/{message.Kind}",
            Qos = 1,
            Payload = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json.Options))),
        }, ct);
        Sent++;
    }
}
