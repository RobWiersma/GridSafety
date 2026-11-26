using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridSafety.Api;
using GridSafety.Core;
using GridSafety.Functions;

namespace GridSafety.Tests;

public class LiveTopicTests
{
    [Theory]
    [InlineData("MISSION#m1", "META", LiveTopics.Missions)]
    [InlineData("FINDING#f1", "META", LiveTopics.Findings)]
    [InlineData("DRONE#d1", "STATE", LiveTopics.Drones)]
    [InlineData("RESOURCES", "CATALOG", LiveTopics.Resources)]
    [InlineData("MISSION#m1", "TLM#2026-01-01T00:00:00Z", null)] // telemetry points: the drone state carries them
    [InlineData("LIVE", "CONN#abc", null)]                       // a socket connecting must not trigger a push
    [InlineData("VISITS#2026-01-01", "C#pages", null)]
    [InlineData("IDEMPOTENCY#page:1", "IDEMPOTENCY", null)]
    [InlineData("SETTINGS", "ALERTS", null)]
    [InlineData(null, null, null)]
    public void Maps_table_keys_to_topics(string? pk, string? sk, string? topic) =>
        Assert.Equal(topic, LiveTopics.ForKey(pk, sk));

    [Fact]
    public void Every_pushed_row_kind_is_in_the_stream_filter()
    {
        foreach (var (pk, sk) in new[] { ("MISSION#x", "META"), ("FINDING#x", "META"), ("DRONE#x", "STATE"), ("RESOURCES", "CATALOG") })
            Assert.Contains(sk, LiveTopics.SortKeys);
    }

    [Fact]
    public void A_batch_becomes_one_sorted_set_of_topics()
    {
        var topics = LiveBroadcaster.Topics([("DRONE#d1", "STATE"), ("MISSION#m1", "META"), ("DRONE#d2", "STATE"), ("MISSION#m1", "TLM#1"), ("LIVE", "CONN#1")]);
        Assert.Equal(["drones", "missions"], topics);
        Assert.Equal("""{"topics":["drones","missions"]}""", LiveTopics.Message(topics));
    }

    private sealed class Recorder : ILiveNotifier
    {
        public List<string> Topics { get; } = [];
        public void Notify(string topic) => Topics.Add(topic);
    }

    [Fact]
    public async Task Repository_writes_signal_their_topic_and_reads_do_not()
    {
        var live = new Recorder();
        var repo = new LiveGridRepository(new InMemoryGridRepository(), live);
        await repo.SaveDroneAsync(new DroneState { DroneId = "d1" });
        await repo.AppendTelemetryAsync("m1", []);
        await repo.ListMissionsAsync();
        await repo.ListDronesAsync();
        Assert.Equal([LiveTopics.Drones], live.Topics);
    }
}

/// <summary>The local push socket end to end: a write through the API reaches a connected browser.</summary>
public class LiveApiTests(ApiTests.Factory factory) : IClassFixture<ApiTests.Factory>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Planning_a_mission_pushes_the_missions_topic()
    {
        var ws = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(factory.Server.BaseAddress, "api/live"), CancellationToken.None);

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Dev-User", "alex");
        (await http.PostAsJsonAsync("/api/missions", new PlanMissionRequest("PITT-MART-115", "DRN-LIVE-1", "Pat"), JsonOpts)).EnsureSuccessStatusCode();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var topics = new HashSet<string>();
        var buffer = new byte[1024];
        while (!topics.Contains(LiveTopics.Missions))
        {
            var r = await ws.ReceiveAsync(buffer, timeout.Token);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
            foreach (var t in doc.RootElement.GetProperty("topics").EnumerateArray()) topics.Add(t.GetString()!);
        }
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [Fact]
    public async Task Plain_http_to_the_socket_is_refused()
    {
        var res = await factory.CreateClient().GetAsync("/api/live");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, res.StatusCode);
    }
}
