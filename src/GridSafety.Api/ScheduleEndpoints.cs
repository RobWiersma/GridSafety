using GridSafety.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GridSafety.Api;

/// <summary>
/// The forward schedule: triaged work orders not sent yet, most important first, each with who'd take it and when.
/// <paramref name="Untriaged"/> findings aren't on it until someone triages them.
/// </summary>
public sealed record ScheduleView(IReadOnlyList<ScheduledJob> Jobs, int Untriaged, DateTimeOffset Now, double ShiftHours, double RestHours);

/// <summary>
/// What "Dispatch what's ready" did: the work orders sent, and any that couldn't go after all, with why.
/// <paramref name="More"/>: it stopped at its time budget with more ready to go.
/// </summary>
public sealed record ScheduleDispatchResult(IReadOnlyList<DispatchedJob> Dispatched, IReadOnlyList<SkippedJob> Skipped, bool More = false);

public sealed record DispatchedJob(string FindingId, string StructureLabel, string Assignee);

public sealed record SkippedJob(string FindingId, string StructureLabel, string Reason);

public static partial class Endpoints
{
    /// <summary>At most this many work orders go out per click (the schedule is planned again before each).</summary>
    private const int MaxDispatchPerRun = 25;

    /// <summary>Stop starting new dispatches after this long, well inside the API's 15 s limit (each one looks up road routes).</summary>
    private static readonly TimeSpan DispatchBudget = TimeSpan.FromSeconds(8);

    private static void MapSchedule(RouteGroupBuilder group, bool canDispatch)
    {
        group.MapGet("/schedule", GetSchedule);
        if (canDispatch) group.MapPost("/schedule/dispatch", DispatchReady);
    }

    private static IReadOnlyList<ScheduledJob> PlanSchedule(IReadOnlyList<Finding> findings, ResourceCatalog catalog, ICircuitCatalog circuits,
        DateTimeOffset now)
    {
        var hours = new FieldHours(FieldWork.History(findings), now);
        var trucks = catalog.Vehicles.Count > 0 ? ReadyTrucks(catalog, hours) : null;
        return WorkSchedule.Plan(findings, catalog.GroundCrews, catalog.Depots, trucks, id => circuits.Find(id)?.VoltageKv, now);
    }

    private static async Task<Ok<ScheduleView>> GetSchedule(IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var catalog = await resources.GetAsync(ct);
        return TypedResults.Ok(new ScheduleView(PlanSchedule(findings, catalog, circuits, now),
            findings.Count(f => f.Status == FindingStatus.New), now, Shifts.LimitHours, Shifts.RestHours));
    }

    /// <summary>
    /// Sends every work order the schedule says can start now, most important first, as if each had been assigned by
    /// hand (same checks: shift hours by road, trucks, qualifications). The schedule is planned again before each one, so
    /// a ride-along or a longer road route that changes who's free is taken into account. Run on demand only. With
    /// <paramref name="findingId"/>, sends just that work order (when it can start now).
    /// </summary>
    private static async Task<Ok<ScheduleDispatchResult>> DispatchReady(string? findingId, HttpContext ctx, IGridRepository repo, IResourceStore resources,
        ICircuitCatalog circuits, IRouteSource routes, TimeProvider clock, CancellationToken ct)
    {
        var sent = new List<DispatchedJob>();
        var skipped = new List<SkippedJob>();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var more = false;
        for (var i = 0; i < MaxDispatchPerRun; i++)
        {
            var now = clock.GetUtcNow();
            var findings = await ListSettledFindingsAsync(repo, now, ct);
            var plan = PlanSchedule(findings, await resources.GetAsync(ct), circuits, now);
            if (plan.FirstOrDefault(j => j.StartsNow && j.Assignee is not null && !tried.Contains(j.FindingId) && (findingId is null || j.FindingId == findingId))
                is not { } next) break;
            if (started.Elapsed > DispatchBudget || i == MaxDispatchPerRun - 1)
            {
                more = true;
                break;
            }
            tried.Add(next.FindingId);
            var result = await ChangeFindingStatus(next.FindingId, new FindingStatusRequest(FindingStatus.Assigned, "Dispatched from the schedule.", next.Assignee),
                ctx, repo, resources, circuits, routes, clock, ct);
            switch (result.Result)
            {
                case Ok<FindingView> ok:
                    sent.Add(new(next.FindingId, next.StructureLabel, ok.Value!.Finding.AssignedTo ?? next.Assignee!));
                    break;
                case ValidationProblem problem:
                    skipped.Add(new(next.FindingId, next.StructureLabel, string.Join(" ", problem.ProblemDetails.Errors.SelectMany(e => e.Value))));
                    break;
                case Conflict<string> conflict:
                    skipped.Add(new(next.FindingId, next.StructureLabel, conflict.Value ?? "Someone else changed it."));
                    break;
                default:
                    skipped.Add(new(next.FindingId, next.StructureLabel, "It couldn't be sent."));
                    break;
            }
        }
        return TypedResults.Ok(new ScheduleDispatchResult(sent, skipped, more));
    }
}
