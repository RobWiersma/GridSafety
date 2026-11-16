using System.Text.Json;
using System.Text.Json.Serialization;

namespace GridSafety.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

public static class Ids
{
    /// <summary>Time-sortable, URL-safe id (timestamp prefix + random suffix).</summary>
    public static string New() =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x11}{Guid.NewGuid():N}"[..20];
}

public sealed record StatusChange<TStatus>(TStatus From, TStatus To, DateTimeOffset At, string? By, string? Note)
    where TStatus : struct, Enum;

/// <summary>A small, explicit state machine. Each aggregate declares its legal moves once.</summary>
public sealed class Workflow<TStatus>(IReadOnlyDictionary<TStatus, TStatus[]> allowed)
    where TStatus : struct, Enum
{
    public IReadOnlyList<TStatus> Next(TStatus from) => allowed[from];
    public bool CanMove(TStatus from, TStatus to) => allowed[from].Contains(to);
    public bool IsTerminal(TStatus s) => allowed[s].Length == 0;

    public void EnsureCanMove(TStatus from, TStatus to)
    {
        if (!CanMove(from, to)) throw new InvalidTransitionException(from.ToString(), to.ToString());
    }
}

public sealed class InvalidTransitionException(string from, string to)
    : InvalidOperationException($"Cannot move from {from} to {to}.");

public sealed class ConcurrencyException(string id)
    : Exception($"{id} was modified by another request. Reload and try again.");

public sealed class NotFoundException(string what) : Exception($"{what} was not found.");
