using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>A truck roll on the map: the work order it's for and where the job stands.</summary>
public sealed record FieldJobView(string FindingId, string StructureLabel, FindingType Type, int Severity, FieldDispatch Dispatch, FieldPhase Phase);

/// <summary>Jobs still out or just back, with the server's clock so the map can animate trucks without drifting.</summary>
public sealed record FieldJobsView(DateTimeOffset Now, IReadOnlyList<FieldJobView> Jobs);

/// <summary>Simulated field work: trucks driving to defects, repairing them and driving back.</summary>
public static partial class Endpoints
{
    /// <summary>How long a truck stays on the map after it gets back to the depot.</summary>
    private static readonly TimeSpan ShowBackFor = TimeSpan.FromSeconds(20);

    private static void MapFieldJobs(RouteGroupBuilder group) => group.MapGet("/field-jobs", ListFieldJobs);

    private static async Task<Ok<FieldJobsView>> ListFieldJobs(IGridRepository repo, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var jobs = findings
            .Where(f => f.Dispatch is { } d && (now < d.ReturnsAt || (d.NextFindingId is null && now < d.ReturnsAt + ShowBackFor)))
            .OrderBy(f => f.Dispatch!.DepartedAt)
            .Select(f => new FieldJobView(f.Id, f.StructureLabel, f.Type, f.Severity, f.Dispatch!, f.Dispatch!.PhaseAt(now)))
            .ToList();
        return TypedResults.Ok(new FieldJobsView(now, jobs));
    }

    /// <summary>
    /// The latest findings, with any work order whose simulated repair has finished closed out first. Field jobs need no
    /// background worker: whoever reads next settles them, stamped with the time the repair actually finished.
    /// </summary>
    private static async Task<IReadOnlyList<Finding>> ListSettledFindingsAsync(IGridRepository repo, DateTimeOffset now, CancellationToken ct)
    {
        var findings = await repo.ListFindingsAsync(1000, ct);
        foreach (var f in findings) await SettleAsync(repo, f, now, ct);
        return findings;
    }

    private static async Task<Finding?> GetSettledFindingAsync(IGridRepository repo, string id, DateTimeOffset now, CancellationToken ct)
    {
        var f = await repo.GetFindingAsync(id, ct);
        if (f is not null) await SettleAsync(repo, f, now, ct);
        return f;
    }

    private static async Task SettleAsync(IGridRepository repo, Finding f, DateTimeOffset now, CancellationToken ct)
    {
        if (!FieldWork.Settle(f, now)) return;
        try
        {
            await repo.SaveFindingAsync(f, ct);
        }
        catch (ConcurrencyException)
        {
            // Another request settled it first; the next read sees that version.
        }
    }
}
