using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using GridSafety.Core;

namespace GridSafety.Api;

/// <summary>
/// Local live push: browsers connect to <c>/api/live</c> and get a topic message a moment after the API writes something.
/// Writes are coalesced for <see cref="Coalesce"/>, so a burst (a telemetry batch saves the drone and the mission) sends
/// one message. In AWS the API runs in Lambda, which can't hold sockets; the LiveBroadcaster Lambda does this job there.
/// </summary>
public sealed class LiveHub(ILogger<LiveHub> log) : ILiveNotifier
{
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(200);
    /// <summary>Plenty for local use; stops a runaway script from holding thousands of sockets.</summary>
    public const int MaxConnections = 200;

    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly Lock _gate = new();
    private HashSet<string> _pending = [];
    private bool _flushScheduled;

    public int Connections => _sockets.Count;

    public void Notify(string topic)
    {
        lock (_gate)
        {
            _pending.Add(topic);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = FlushAfterDelayAsync();
    }

    private async Task FlushAfterDelayAsync()
    {
        await Task.Delay(Coalesce);
        HashSet<string> topics;
        lock (_gate)
        {
            topics = _pending;
            _pending = [];
            _flushScheduled = false;
        }
        if (topics.Count == 0 || _sockets.IsEmpty) return;

        var bytes = Encoding.UTF8.GetBytes(LiveTopics.Message(topics));
        foreach (var (id, socket) in _sockets)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                _sockets.TryRemove(id, out _); // gone; its receive loop finishes the cleanup
            }
        }
    }

    /// <summary>Holds one browser's socket until it closes. Browsers only listen; anything they send is ignored.</summary>
    public async Task RunAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (_sockets.Count >= MaxConnections) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _sockets[id] = socket;
        var buffer = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, ctx.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            log.LogDebug("Live socket {Id} dropped", id);
        }
        finally
        {
            _sockets.TryRemove(id, out _);
        }
    }
}

internal static class LiveRegistration
{
    /// <summary>Wraps the repository and resource store so their writes signal <see cref="LiveHub"/>.</summary>
    public static void AddLocalLivePush(this IServiceCollection services)
    {
        services.AddSingleton<LiveHub>();
        services.AddSingleton<ILiveNotifier>(sp => sp.GetRequiredService<LiveHub>());
        Decorate<IGridRepository>(services, (sp, inner) => new LiveGridRepository(inner, sp.GetRequiredService<ILiveNotifier>()));
        Decorate<IResourceStore>(services, (sp, inner) => new LiveResourceStore(inner, sp.GetRequiredService<ILiveNotifier>()));
    }

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> wrap) where T : class
    {
        var d = services.Last(s => s.ServiceType == typeof(T));
        services.Remove(d);
        services.AddSingleton<T>(sp => wrap(sp, (T)(d.ImplementationInstance
            ?? d.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!))));
    }
}
