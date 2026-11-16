// GridSafety drone simulator.
//
// Local (no AWS):  dotnet run --project tools/GridSafety.Simulator
//                  creates a mission through the local API and streams telemetry to /api/ingest.
// AWS IoT Core:    dotnet run --project tools/GridSafety.Simulator -- --aws --mission <id> --circuit <id> --drone <id>
//                  publishes MQTT messages to gridsafety/drones/{drone}/{kind} with your AWS credentials.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using GridSafety.Core;
using GridSafety.Core.Simulation;

var opts = Options.Parse(args);
if (opts.Help)
{
    Console.WriteLine("""
        GridSafety drone simulator

          --circuit <id>     Circuit to patrol (default PITT-MART-115). Run --list to see all
          --drone <id>       Drone id (default DRN-07)
          --mission <id>     Existing mission id (required with --aws; locally one is created for you)
          --api <url>        Local API base URL (default http://localhost:5080)
          --aws              Publish to AWS IoT Core instead of the local API
          --speed <x>        Playback speed multiplier (default 1)
          --seed <n>         Random seed for a reproducible flight
          --no-incidents     Skip the scripted corridor breach / altitude excursion
          --list             List the circuits (real OpenStreetMap lines) and exit
        """);
    return 0;
}

if (args.Contains("--list"))
{
    foreach (var c in StaticCircuitCatalog.Instance.All)
        Console.WriteLine($"{c.Id,-18} {c.Name,-52} {(c.VoltageKv > 0 ? $"{c.VoltageKv,4} kV" : "   ? kV")}  {c.Structures.Count,3} structures  {c.LengthMeters / 1000:0.0} km");
    return 0;
}

var circuit = StaticCircuitCatalog.Instance.Find(opts.Circuit)
              ?? throw new ArgumentException($"Unknown circuit {opts.Circuit}");
var rng = opts.Seed is { } seed ? new Random(seed) : new Random();

await using ISimulatorLink link = opts.Aws ? await IotCoreLink.CreateAsync(opts.Drone) : new LocalApiLink(opts.Api);
var missionId = opts.Mission ?? await link.PlanMissionAsync(circuit.Id, opts.Drone);
if (missionId is null)
{
    Console.Error.WriteLine("--mission is required with --aws (plan the mission in the console first).");
    return 1;
}

Console.WriteLine($"✈  {opts.Drone} patrolling {circuit.Name} ({circuit.Structures.Count} structures, {circuit.LengthMeters / 1000:0.0} km), mission {missionId}");

await DemoFlight.FlyAsync(circuit, opts.Drone, missionId, link, TimeSpan.FromMilliseconds(400 / opts.Speed), rng, TimeProvider.System,
    scriptedIncidents: !opts.NoIncidents,
    onPoint: (i, n, p) => Console.Write($"\r   {i + 1,4}/{n}  {p.Lat:0.00000},{p.Lon:0.00000}  alt {p.AltitudeM,5:0} m  batt {p.BatteryPct,3:0}%  rssi {p.RssiDbm,4:0} dBm   "),
    onFinding: (s, f) => Console.WriteLine($"\n   ⚠ sev {f.Severity} {f.Type} at {s.Label}"));

Console.WriteLine($"\n🛬 Landed. Mission {missionId} complete.");
return 0;

internal sealed record Options(string Circuit, string Drone, string? Mission, string Api, bool Aws, double Speed, int? Seed, bool NoIncidents, bool Help)
{
    public static Options Parse(string[] args)
    {
        string? Val(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        return new Options(
            Val("--circuit") ?? "PITT-MART-115",
            Val("--drone") ?? "DRN-07",
            Val("--mission"),
            (Val("--api") ?? "http://localhost:5080").TrimEnd('/'),
            args.Contains("--aws"),
            double.TryParse(Val("--speed"), System.Globalization.CultureInfo.InvariantCulture, out var sp) && sp > 0 ? sp : 1,
            int.TryParse(Val("--seed"), out var sd) ? sd : null,
            args.Contains("--no-incidents"),
            args.Contains("--help") || args.Contains("-h"));
    }
}

internal interface ISimulatorLink : IDroneLink, IAsyncDisposable
{
    Task<string?> PlanMissionAsync(string circuitId, string droneId);
}

/// <summary>Local dev: talks to the API's dev-only /api/ingest endpoint.</summary>
internal sealed class LocalApiLink : ISimulatorLink
{
    private readonly HttpClient _http;

    public LocalApiLink(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        _http.DefaultRequestHeaders.Add("X-Dev-User", "simulator");
    }

    public async Task<string?> PlanMissionAsync(string circuitId, string droneId)
    {
        var res = await _http.PostAsJsonAsync("api/missions", new { circuitId, droneId, pilot = "Sim Pilot" }, Json.Options);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Could not plan mission: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("mission").GetProperty("id").GetString();
    }

    public async Task SendAsync(DroneMessage message, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/ingest", message, Json.Options, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Ingest failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>AWS: publishes to IoT Core over HTTPS (SigV4). The IoT rule routes messages to the ingest Lambda.</summary>
internal sealed class IotCoreLink(AmazonIotDataClient data, string droneId) : ISimulatorLink
{
    public static async Task<IotCoreLink> CreateAsync(string droneId)
    {
        using var iot = new AmazonIoTClient();
        var endpoint = await iot.DescribeEndpointAsync(new DescribeEndpointRequest { EndpointType = "iot:Data-ATS" });
        Console.WriteLine($"   IoT Core endpoint: {endpoint.EndpointAddress}");
        return new IotCoreLink(new AmazonIotDataClient($"https://{endpoint.EndpointAddress}"), droneId);
    }

    public Task<string?> PlanMissionAsync(string circuitId, string droneId) => Task.FromResult<string?>(null);

    public async Task SendAsync(DroneMessage message, CancellationToken ct = default)
    {
        await data.PublishAsync(new PublishRequest
        {
            Topic = $"gridsafety/drones/{droneId}/{message.Kind}",
            Qos = 1,
            Payload = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json.Options))),
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        data.Dispose();
        return ValueTask.CompletedTask;
    }
}
