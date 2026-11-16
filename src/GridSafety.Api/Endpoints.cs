using Amazon.S3;
using Amazon.S3.Model;
using System.Security.Cryptography;
using GridSafety.Core;
using GridSafety.Core.Simulation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace GridSafety.Api;

public sealed record PlanMissionRequest(string CircuitId, string DroneId, string Pilot);
public sealed record MissionStatusRequest(MissionStatus Status, string? Note);
public sealed record FindingStatusRequest(FindingStatus Status, string? Note, string? Assignee);

public sealed record MissionView(Mission Mission, string CircuitName, double Coverage, IReadOnlyList<MissionStatus> NextStatuses);
public sealed record FindingView(Finding Finding, string CircuitName, IReadOnlyList<FindingStatus> NextStatuses, bool Overdue, string SeverityLabel);
public sealed record MeView(string Id, string Name, bool IsSupervisor, IReadOnlyList<string> Groups);
public sealed record UrlView(string Url, DateTimeOffset ExpiresAt);

public static class Endpoints
{
    public static void MapGridSafetyApi(this IEndpointRouteBuilder routes, bool devIngest)
    {
        var api = routes.MapGroup("/api");
        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var secured = api.MapGroup("/").AddEndpointFilter(RequireUser);
        secured.MapGet("/me", (HttpContext ctx) => { var u = User(ctx); return TypedResults.Ok(new MeView(u.Id, u.DisplayName, u.IsSupervisor, u.Groups)); });
        secured.MapGet("/circuits", (ICircuitCatalog c) => TypedResults.Ok(c.All));
        secured.MapGet("/stats", Stats);
        secured.MapGet("/drones", async (IGridRepository repo, CancellationToken ct) => TypedResults.Ok(await repo.ListDronesAsync(ct)));

        secured.MapGet("/missions", ListMissions);
        secured.MapPost("/missions", PlanMission);
        secured.MapGet("/missions/{id}", GetMission);
        secured.MapPost("/missions/{id}/status", ChangeMissionStatus);
        secured.MapGet("/missions/{id}/track", GetTrack);
        secured.MapGet("/missions/{id}/report-url", GetReportUrl);

        secured.MapPost("/demo-flights", StartDemoFlight);

        secured.MapGet("/findings", ListFindings);
        secured.MapGet("/findings/{id}", GetFinding);
        secured.MapPost("/findings/{id}/status", ChangeFindingStatus);

        if (devIngest)
        {
            // Local stand-in for AWS IoT Core so the simulator works without an AWS account.
            secured.MapPost("/ingest", async (DroneMessage msg, DroneIngestService ingest, CancellationToken ct) =>
            {
                try { return Results.Ok(await ingest.HandleAsync(msg, ct)); }
                catch (NotFoundException ex) { return Results.NotFound(ex.Message); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return Results.BadRequest(ex.Message); }
            });
        }
    }

    private static async ValueTask<object?> RequireUser(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var user = UserResolver.Resolve(ctx.HttpContext);
        if (user is null) return Results.Unauthorized();
        ctx.HttpContext.Items[nameof(CurrentUser)] = user;
        return await next(ctx);
    }

    private static CurrentUser User(HttpContext ctx) => (CurrentUser)ctx.Items[nameof(CurrentUser)]!;

    private static MissionView ToView(Mission m, ICircuitCatalog circuits)
    {
        var circuit = circuits.Find(m.CircuitId);
        return new MissionView(m, circuit?.Name ?? m.CircuitId, circuit is null ? 0 : m.Coverage(circuit), Mission.Workflow.Next(m.Status));
    }

    private static FindingView ToView(Finding f, ICircuitCatalog circuits, DateTimeOffset now) =>
        new(f, circuits.Find(f.CircuitId)?.Name ?? f.CircuitId, Finding.Workflow.Next(f.Status), f.IsOverdue(now), TriageSla.Describe(f.Severity));

    private static async Task<Ok<OpsStats>> Stats(IGridRepository repo, TimeProvider clock, CancellationToken ct)
    {
        var missions = await repo.ListMissionsAsync(500, ct);
        var findings = await repo.ListFindingsAsync(1000, ct);
        var drones = await repo.ListDronesAsync(ct);
        return TypedResults.Ok(OpsStats.Compute(missions, findings, drones, clock.GetUtcNow()));
    }

    private static async Task<Ok<IEnumerable<MissionView>>> ListMissions(IGridRepository repo, ICircuitCatalog circuits, CancellationToken ct) =>
        TypedResults.Ok((await repo.ListMissionsAsync(100, ct)).Select(m => ToView(m, circuits)));

    private static async Task<Results<Created<MissionView>, ValidationProblem, Conflict<string>>> PlanMission(
        PlanMissionRequest req, HttpContext ctx, IGridRepository repo, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var circuit = circuits.Find(req.CircuitId ?? "");
        if (circuit is null) errors["circuitId"] = ["Unknown circuit."];
        if (string.IsNullOrWhiteSpace(req.DroneId) || req.DroneId.Length > 40 || !req.DroneId.All(c => char.IsLetterOrDigit(c) || c == '-'))
            errors["droneId"] = ["Drone ID is required: letters, digits and dashes, max 40."];
        if (string.IsNullOrWhiteSpace(req.Pilot) || req.Pilot.Length > 100) errors["pilot"] = ["Pilot in command is required."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        // A drone can only fly one mission at a time.
        var busy = (await repo.ListMissionsAsync(200, ct))
            .FirstOrDefault(m => m.DroneId.Equals(req.DroneId.Trim(), StringComparison.OrdinalIgnoreCase) && !Mission.Workflow.IsTerminal(m.Status));
        if (busy is not null) return TypedResults.Conflict($"{req.DroneId} is already assigned to mission {busy.Id} ({busy.Status}).");

        var mission = Mission.Plan(circuit!, req.DroneId, req.Pilot, User(ctx).DisplayName, clock.GetUtcNow());
        await repo.SaveMissionAsync(mission, ct);
        return TypedResults.Created($"/api/missions/{mission.Id}", ToView(mission, circuits));
    }

    /// <summary>
    /// Supervisor-only, on demand: plans a mission for the demo drone and starts a simulated flight.
    /// One demo flight at a time; a demo mission that stopped reporting for 10 minutes is aborted and replaced.
    /// </summary>
    private static async Task<Results<Accepted<MissionView>, ValidationProblem, Conflict<string>, ProblemHttpResult>> StartDemoFlight(
        DemoFlightRequest? req, HttpContext ctx, IGridRepository repo, ICircuitCatalog circuits, IDemoFlightLauncher launcher,
        TimeProvider clock, CancellationToken ct)
    {
        var user = User(ctx);
        if (!user.IsSupervisor)
            return TypedResults.Problem("Only supervisors can launch demo flights.", statusCode: StatusCodes.Status403Forbidden);
        if (!launcher.Enabled)
            return TypedResults.Problem("Demo flights are not configured for this deployment.", statusCode: StatusCodes.Status503ServiceUnavailable);

        Circuit? circuit;
        if (req?.CircuitId is { Length: > 0 } circuitId)
        {
            circuit = circuits.Find(circuitId);
            if (circuit is null) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["circuitId"] = ["Unknown circuit."] });
        }
        else
        {
            circuit = circuits.All[RandomNumberGenerator.GetInt32(circuits.All.Count)];
        }

        var now = clock.GetUtcNow();
        var active = (await repo.ListMissionsAsync(200, ct))
            .Where(m => m.DroneId.Equals(DemoFlight.DroneId, StringComparison.OrdinalIgnoreCase) && !Mission.Workflow.IsTerminal(m.Status));
        foreach (var m in active)
        {
            if (!DemoFlight.IsStale(m, now))
                return TypedResults.Conflict($"The demo drone is already on mission {m.Id} ({circuits.Find(m.CircuitId)?.Name ?? m.CircuitId}). Try again when it lands.");
            m.MoveTo(MissionStatus.Aborted, now, "system", "Demo flight stopped reporting");
            await repo.SaveMissionAsync(m, ct);
        }

        var mission = Mission.Plan(circuit, DemoFlight.DroneId, DemoFlight.Pilot, user.DisplayName, now);
        await repo.SaveMissionAsync(mission, ct);
        try
        {
            await launcher.LaunchAsync(new DemoFlightLaunch(mission.Id, circuit.Id, mission.DroneId), ct);
        }
        catch
        {
            mission.MoveTo(MissionStatus.Aborted, clock.GetUtcNow(), "system", "Demo flight failed to launch");
            await repo.SaveMissionAsync(mission, CancellationToken.None);
            throw;
        }
        return TypedResults.Accepted($"/api/missions/{mission.Id}", ToView(mission, circuits));
    }

    private static async Task<Results<Ok<MissionView>, NotFound>> GetMission(string id, IGridRepository repo, ICircuitCatalog circuits, CancellationToken ct) =>
        await repo.GetMissionAsync(id, ct) is { } m ? TypedResults.Ok(ToView(m, circuits)) : TypedResults.NotFound();

    private static async Task<Results<Ok<MissionView>, NotFound, ValidationProblem, Conflict<string>>> ChangeMissionStatus(
        string id, MissionStatusRequest req, HttpContext ctx, IGridRepository repo, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var m = await repo.GetMissionAsync(id, ct);
        if (m is null) return TypedResults.NotFound();
        try
        {
            m.MoveTo(req.Status, clock.GetUtcNow(), User(ctx).DisplayName, req.Note);
            await repo.SaveMissionAsync(m, ct);
        }
        catch (InvalidTransitionException ex) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [ex.Message] }); }
        catch (ConcurrencyException ex) { return TypedResults.Conflict(ex.Message); }
        return TypedResults.Ok(ToView(m, circuits));
    }

    private static async Task<Results<Ok<IReadOnlyList<TelemetryPoint>>, NotFound>> GetTrack(string id, IGridRepository repo, CancellationToken ct) =>
        await repo.GetMissionAsync(id, ct) is null ? TypedResults.NotFound() : TypedResults.Ok(await repo.GetTrackAsync(id, ct));

    private static async Task<Results<Ok<UrlView>, NotFound>> GetReportUrl(
        string id, IGridRepository repo, S3Presigner s3, IOptions<ApiOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var m = await repo.GetMissionAsync(id, ct);
        if (m?.ReportKey is null || options.Value.ReportsBucket is null) return TypedResults.NotFound();
        var expires = clock.GetUtcNow().AddMinutes(5);
        var url = await s3.Client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = options.Value.ReportsBucket,
            Key = m.ReportKey,
            Verb = HttpVerb.GET,
            Expires = expires.UtcDateTime,
        });
        return TypedResults.Ok(new UrlView(url, expires));
    }

    private static async Task<Ok<IEnumerable<FindingView>>> ListFindings(
        string? status, string? missionId, IGridRepository repo, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await repo.ListFindingsAsync(1000, ct);
        var filtered = all.Where(f =>
            (missionId is null || f.MissionId == missionId) &&
            (status switch
            {
                null or "" or "all" => true,
                "open" => !Finding.Workflow.IsTerminal(f.Status),
                _ => Enum.TryParse<FindingStatus>(status, true, out var s) && f.Status == s,
            }));

        // Work queue order: overdue first, then severity, then oldest.
        return TypedResults.Ok(filtered
            .OrderByDescending(f => f.IsOverdue(now))
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.CreatedAt)
            .Select(f => ToView(f, circuits, now)));
    }

    private static async Task<Results<Ok<FindingView>, NotFound>> GetFinding(string id, IGridRepository repo, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct) =>
        await repo.GetFindingAsync(id, ct) is { } f ? TypedResults.Ok(ToView(f, circuits, clock.GetUtcNow())) : TypedResults.NotFound();

    private static async Task<Results<Ok<FindingView>, NotFound, ValidationProblem, Conflict<string>, ProblemHttpResult>> ChangeFindingStatus(
        string id, FindingStatusRequest req, HttpContext ctx, IGridRepository repo, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var user = User(ctx);
        if (Finding.RequiresSupervisor(req.Status) && !user.IsSupervisor)
            return TypedResults.Problem($"Only supervisors can move a finding to {req.Status}.", statusCode: StatusCodes.Status403Forbidden);

        var f = await repo.GetFindingAsync(id, ct);
        if (f is null) return TypedResults.NotFound();
        try
        {
            f.MoveTo(req.Status, clock.GetUtcNow(), user.DisplayName, req.Note, req.Assignee);
            await repo.SaveFindingAsync(f, ct);
        }
        catch (InvalidTransitionException ex) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [ex.Message] }); }
        catch (ArgumentException ex) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assignee"] = [ex.Message] }); }
        catch (ConcurrencyException ex) { return TypedResults.Conflict(ex.Message); }
        return TypedResults.Ok(ToView(f, circuits, clock.GetUtcNow()));
    }
}
