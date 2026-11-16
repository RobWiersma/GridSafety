using GridSafety.Core;
using GridSafety.Core.Simulation;

namespace GridSafety.Tests;

public class DemoFlightTests
{
    private sealed class RecordingLink : IDroneLink
    {
        public List<DroneMessage> Sent { get; } = [];

        public Task SendAsync(DroneMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Flies_the_whole_line_reports_a_severe_defect_then_lands()
    {
        var link = new RecordingLink();
        await DemoFlight.FlyAsync(TestCircuits.Line, "DRN-T", "m1", link, TimeSpan.Zero, new Random(42), TimeProvider.System);

        Assert.Equal(MessageKinds.Telemetry, link.Sent[0].Kind);
        Assert.Equal(MessageKinds.Landed, link.Sent[^1].Kind);
        Assert.Single(link.Sent, m => m.Kind == MessageKinds.Landed);
        Assert.All(link.Sent, m => Assert.Equal(("DRN-T", "m1"), (m.DroneId, m.MissionId)));
        Assert.Contains(link.Sent, m => m.Finding is { Severity: >= 4 });

        var lastFix = link.Sent.Last(m => m.Kind == MessageKinds.Telemetry).Points![0].Position;
        Assert.True(Geo.DistanceMeters(lastFix, TestCircuits.Line.Path[^1]) < 20, "drone should reach the end of the line");
    }

    [Fact]
    public async Task Lands_early_when_told_to_stop()
    {
        var link = new RecordingLink();
        var budget = 5;
        await DemoFlight.FlyAsync(TestCircuits.Line, "DRN-T", "m1", link, TimeSpan.Zero, new Random(1), TimeProvider.System,
            keepFlying: () => budget-- > 0);

        Assert.Equal(5, link.Sent.Count(m => m.Kind == MessageKinds.Telemetry));
        Assert.Equal(MessageKinds.Landed, link.Sent[^1].Kind);
    }

    [Fact]
    public void Same_seed_gives_the_same_flight()
    {
        var a = FlightPlan.Build(TestCircuits.Line, new Random(7), scriptedIncidents: true, Now);
        var b = FlightPlan.Build(TestCircuits.Line, new Random(7), scriptedIncidents: true, Now);
        Assert.Equal(a.Points, b.Points);
        Assert.Equal(a.SevereAt, b.SevereAt);
    }

    [Fact]
    public void Scripted_incidents_trigger_a_corridor_breach_otherwise_the_drone_stays_inside()
    {
        var withIncidents = FlightPlan.Build(TestCircuits.Line, new Random(3), scriptedIncidents: true, Now);
        var clean = FlightPlan.Build(TestCircuits.Line, new Random(3), scriptedIncidents: false, Now);

        Assert.Contains(withIncidents.Points, p => FlightRules.Evaluate(p, TestCircuits.Line).Any(a => a.Kind == AlertKind.CorridorBreach));
        Assert.DoesNotContain(clean.Points, p => FlightRules.Evaluate(p, TestCircuits.Line).Any(a => a.Kind is AlertKind.CorridorBreach or AlertKind.AltitudeCeiling));
    }

    [Fact]
    public void Demo_mission_is_stale_only_when_unfinished_and_silent_for_ten_minutes()
    {
        var m = Mission.Plan(TestCircuits.Line, DemoFlight.DroneId, DemoFlight.Pilot, "sup", Now);

        Assert.False(DemoFlight.IsStale(m, Now.AddMinutes(9)));
        Assert.True(DemoFlight.IsStale(m, Now.AddMinutes(11)));

        m.MoveTo(MissionStatus.Aborted, Now);
        Assert.False(DemoFlight.IsStale(m, Now.AddHours(1)));
    }

    /// <summary>Fails the first <paramref name="failures"/> sends with <paramref name="error"/>, then records.</summary>
    private sealed class FlakyLink(int failures, Func<Exception> error) : IDroneLink
    {
        public int Attempts { get; private set; }
        public List<DroneMessage> Sent { get; } = [];

        public Task SendAsync(DroneMessage message, CancellationToken ct = default)
        {
            if (++Attempts <= failures) throw error();
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static readonly TimeSpan[] NoWait = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    private static readonly DroneMessage Ping = new() { Kind = MessageKinds.Landed, DroneId = DemoFlight.DroneId, MissionId = "m1" };

    [Fact]
    public async Task Retry_rides_out_an_endpoint_that_is_not_reachable_yet()
    {
        // What a new account's IoT endpoint looked like: socket errors wrapped in HttpRequestException.
        var flaky = new FlakyLink(2, () => new HttpRequestException("Unknown socket error", new System.Net.Sockets.SocketException()));
        var retries = new List<int>();

        await new RetryingDroneLink(flaky, NoWait, TimeProvider.System, (_, n) => retries.Add(n)).SendAsync(Ping);

        Assert.Single(flaky.Sent);
        Assert.Equal([1, 2], retries);
    }

    [Fact]
    public async Task Retry_gives_up_after_the_last_delay()
    {
        var flaky = new FlakyLink(10, () => new HttpRequestException("down"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new RetryingDroneLink(flaky, NoWait, TimeProvider.System).SendAsync(Ping));
        Assert.Equal(NoWait.Length + 1, flaky.Attempts);
    }

    [Fact]
    public async Task Retry_does_not_repeat_errors_that_are_not_network_failures()
    {
        var flaky = new FlakyLink(1, () => new InvalidOperationException("bad message"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RetryingDroneLink(flaky, NoWait, TimeProvider.System).SendAsync(Ping));
        Assert.Equal(1, flaky.Attempts);
    }
}
