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
public sealed record FindingView(Finding Finding, string CircuitName, IReadOnlyList<FindingStatus> NextStatuses, bool Overdue, string SeverityLabel, string SuggestedCrew);
/// <summary>A ground crew with its open work orders and the jobs its people are out on right now.</summary>
public sealed record CrewView(Crew Crew, int OpenWorkOrders, IReadOnlyList<FieldDispatch>? InField = null, IReadOnlyList<MemberShift>? Shifts = null);
/// <summary>
/// One technician's hours this shift, in simulated hours (out of <see cref="Shifts.LimitHours"/>). Worked counts up to
/// now; booked adds the jobs already lined up. RestingUntil is set while they're off after a full shift.
/// </summary>
public sealed record MemberShift(string Member, double WorkedHours, double BookedHours, double LeftHours, DateTimeOffset? StartedAt, DateTimeOffset? RestingUntil);
/// <summary>Someone a work order can go to, nearest first: a technician (<see cref="Member"/> set) or a whole crew.</summary>
public sealed record AssigneeOption(string Label, string CrewId, string CrewName, string? Member, string? Role,
    string? DepotId, string? DepotName, double? DistanceKm, int CrewOpenWorkOrders, DateTimeOffset? BusyUntil, string? AfterJob,
    double? HoursLeft = null, double? JobHours = null, DateTimeOffset? RestingUntil = null)
{
    /// <summary>Not enough of their shift left for this job (resting people are marked by <see cref="RestingUntil"/> instead).</summary>
    public bool OutOfHours => RestingUntil is null && HoursLeft is { } left && JobHours is { } job && job > left;
}
public sealed record AlertAckRequest(DateTimeOffset? At, AlertKind? Kind);
public sealed record VisitRequest(string? Page, string? Referrer, bool SignedIn);
public sealed record CountView(string Name, int Count);
public sealed record DayVisitsView(DateOnly Day, int PageViews, int UniqueVisitors);
public sealed record VisitorStatsView(
    int Days, int PageViews, int VisitorDays, int SignedInVisitorDays, IReadOnlyList<DayVisitsView> Daily,
    IReadOnlyList<CountView> Pages, IReadOnlyList<CountView> Referrers, IReadOnlyList<CountView> Devices,
    IReadOnlyList<CountView> Browsers, IReadOnlyList<CountView> OperatingSystems, IReadOnlyList<CountView> Countries,
    IReadOnlyList<CountView> Languages);
public sealed record MeView(string Id, string Name, bool IsSupervisor, IReadOnlyList<string> Groups, bool IsGuest = false);
public sealed record UrlView(string Url, DateTimeOffset ExpiresAt);

public static partial class Endpoints
{
    public static void MapGridSafetyApi(this IEndpointRouteBuilder routes, bool devIngest)
    {
        var api = routes.MapGroup("/api");
        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Guests: a read-only copy of the GET routes, no sign-in needed. API Gateway sends GET /api/public/* to the
        // API without the JWT authorizer; only GETs are mapped here, so nothing can be changed, and GuestView
        // strips who-did-what before it leaves. Must be mapped before the secured catch-all group.
        // Anonymous page-view beacon for every visitor, signed in or not (see VisitAnonymizer).
        api.MapPost("/public/visit", RecordVisit);

        var guest = api.MapGroup("/public").AddEndpointFilter(GuestView.Filter);
        guest.MapGet("/me", () => TypedResults.Ok(new MeView("guest", "Guest", false, [], IsGuest: true)));
        guest.MapGet("/circuits", (ICircuitCatalog c) => TypedResults.Ok(c.All));
        guest.MapGet("/stats", Stats);
        guest.MapGet("/drones", async (IGridRepository repo, CancellationToken ct) => TypedResults.Ok(await repo.ListDronesAsync(ct)));
        guest.MapGet("/missions", ListMissions);
        guest.MapGet("/missions/{id}", GetMission);
        guest.MapGet("/missions/{id}/track", GetTrack);
        guest.MapGet("/missions/{id}/report-url", GetReportUrl);
        guest.MapGet("/findings", ListFindings);
        guest.MapGet("/findings/{id}", GetFinding);
        guest.MapGet("/findings/{id}/image", GetFindingImage);
        guest.MapGet("/findings/{id}/aerial", GetFindingAerial);
        guest.MapGet("/findings/{id}/assignees", ListAssignees);
        guest.MapGet("/crews", ListCrews);
        guest.MapGet("/resources", ListResources);
        MapFieldJobs(guest);

        var secured = api.MapGroup("/").AddEndpointFilter(RequireUser);
        secured.MapGet("/me", (HttpContext ctx) => { var u = User(ctx); return TypedResults.Ok(new MeView(u.Id, u.DisplayName, u.IsSupervisor, u.Groups)); });
        secured.MapGet("/circuits", (ICircuitCatalog c) => TypedResults.Ok(c.All));
        secured.MapGet("/stats", Stats);
        secured.MapGet("/drones", async (IGridRepository repo, CancellationToken ct) => TypedResults.Ok(await repo.ListDronesAsync(ct)));

        secured.MapGet("/missions", ListMissions);
        secured.MapPost("/missions", PlanMission);
        secured.MapGet("/missions/{id}", GetMission);
        secured.MapPost("/missions/{id}/status", ChangeMissionStatus);
        secured.MapPost("/missions/{id}/alerts/ack", AcknowledgeAlerts);
        secured.MapPost("/missions/{id}/simulate", SimulateMission);
        secured.MapGet("/missions/{id}/track", GetTrack);
        secured.MapGet("/missions/{id}/report-url", GetReportUrl);

        secured.MapPost("/demo-flights", StartDemoFlight);

        secured.MapGet("/findings", ListFindings);
        secured.MapGet("/findings/{id}", GetFinding);
        secured.MapGet("/findings/{id}/image", GetFindingImage);
        secured.MapGet("/findings/{id}/aerial", GetFindingAerial);
        secured.MapGet("/findings/{id}/assignees", ListAssignees);
        secured.MapPost("/findings/{id}/status", ChangeFindingStatus);
        secured.MapGet("/crews", ListCrews);
        MapFieldJobs(secured);
        MapResources(secured);
        MapAlertSettings(secured);
        MapSimulationSettings(secured);
        secured.MapGet("/visitors", GetVisitorStats);

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

    /// <summary>The suggested assignee is the nearest on-duty technician qualified for the defect who isn't out on another job.</summary>
    private static FindingView ToView(Finding f, ICircuitCatalog circuits, DateTimeOffset now, ResourceCatalog catalog, FieldHours hours) =>
        new(f, circuits.Find(f.CircuitId)?.Name ?? f.CircuitId, Finding.Workflow.Next(f.Status), f.IsOverdue(now), TriageSla.Describe(f.Severity),
            CrewRoster.Candidates(catalog.GroundCrews, catalog.Depots, f.Type, f.Location, ActiveOf(hours), hours with { Severity = f.Severity })
                .FirstOrDefault(c => c.Available)?.Label ?? "");

    private static IReadOnlyList<FieldDispatch> ActiveOf(FieldHours hours) => [.. hours.History.Where(d => d.IsActive(hours.Now))];

    /// <summary>Every member's shift, for the crew cards and the shift board.</summary>
    internal static IReadOnlyList<MemberShift> MemberShifts(Crew c, FieldHours hours) =>
    [
        .. c.Members.Select(m =>
        {
            var s = hours.Of(c, m);
            return new MemberShift(m.Name, Shifts.Hours(s.Worked), Shifts.Hours(s.Booked), Shifts.Hours(s.Left), s.StartedAt, s.RestingUntil);
        }),
    ];

    private static async Task<Ok<OpsStats>> Stats(IGridRepository repo, TimeProvider clock, CancellationToken ct)
    {
        var missions = await repo.ListMissionsAsync(500, ct);
        var findings = await ListSettledFindingsAsync(repo, clock.GetUtcNow(), ct);
        var drones = await repo.ListDronesAsync(ct);
        return TypedResults.Ok(OpsStats.Compute(missions, findings, drones, clock.GetUtcNow()));
    }

    private static async Task<Ok<IEnumerable<MissionView>>> ListMissions(IGridRepository repo, ICircuitCatalog circuits, CancellationToken ct) =>
        TypedResults.Ok((await repo.ListMissionsAsync(100, ct)).Select(m => ToView(m, circuits)));

    private static async Task<Results<Created<MissionView>, ValidationProblem, Conflict<string>>> PlanMission(
        PlanMissionRequest req, HttpContext ctx, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var circuit = circuits.Find(req.CircuitId ?? "");
        if (circuit is null) errors["circuitId"] = ["Unknown circuit."];
        if (string.IsNullOrWhiteSpace(req.DroneId) || req.DroneId.Length > 40 || !req.DroneId.All(c => char.IsLetterOrDigit(c) || c == '-'))
            errors["droneId"] = ["Drone ID is required: letters, digits and dashes, max 40."];
        if (string.IsNullOrWhiteSpace(req.Pilot) || req.Pilot.Length > 100) errors["pilot"] = ["Pilot in command is required."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        // Fleet drones in maintenance or retired stay on the ground. (IDs outside the fleet, e.g. a contractor's drone, are allowed.)
        if ((await resources.GetAsync(ct)).FindDrone(req.DroneId) is { Status: not DroneAssetStatus.Active } grounded)
            return TypedResults.Conflict($"{grounded.Id} is {grounded.Status.ToString().ToLowerInvariant()} and can't fly.");

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
        DemoFlightRequest? req, HttpContext ctx, IGridRepository repo, IResourceStore resources, ISimulationSettingsStore simulation, ICircuitCatalog circuits, IDemoFlightLauncher launcher,
        TimeProvider clock, CancellationToken ct)
    {
        var user = User(ctx);
        if (!user.IsSupervisor)
            return TypedResults.Problem("Only supervisors can launch demo flights.", statusCode: StatusCodes.Status403Forbidden);
        if (!launcher.Enabled)
            return TypedResults.Problem("Demo flights are not configured for this deployment.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var catalog = await resources.GetAsync(ct);
        if (catalog.FindDrone(DemoFlight.DroneId) is { Status: not DroneAssetStatus.Active } demoDrone)
            return TypedResults.Conflict($"The demo drone {demoDrone.Id} is {demoDrone.Status.ToString().ToLowerInvariant()}. Set it back to active under Resources to fly.");

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
        var recent = await repo.ListMissionsAsync(1000, ct);
        var active = recent
            .Where(m => m.DroneId.Equals(DemoFlight.DroneId, StringComparison.OrdinalIgnoreCase) && !Mission.Workflow.IsTerminal(m.Status));
        foreach (var m in active)
        {
            if (!DemoFlight.IsStale(m, now))
                return TypedResults.Conflict($"The demo drone is already flying {m.Name ?? m.Id} ({circuits.Find(m.CircuitId)?.Name ?? m.CircuitId}). Try again when it lands.");
            m.MoveTo(MissionStatus.Aborted, now, "system", "Demo flight stopped reporting");
            await repo.SaveMissionAsync(m, ct);
        }

        if (recent.Count(m => m.SimulatedAt is not null && !Mission.Workflow.IsTerminal(m.Status) && !DemoFlight.IsStale(m, now)) >= MaxSimulatedFlights)
            return TypedResults.Conflict($"{MaxSimulatedFlights} simulated flights are already in the air. Try again when one lands.");

        var mission = Mission.Plan(circuit, DemoFlight.DroneId, DemoFlight.PickPilot(catalog.FlightCrews, recent), user.DisplayName, now,
            name: FlightNames.Generate(Random.Shared, recent.Select(m => m.Name)));
        mission.SimulatedAt = now;
        await repo.SaveMissionAsync(mission, ct);
        try
        {
            var sim = await simulation.GetAsync(ct);
            await launcher.LaunchAsync(new DemoFlightLaunch(mission.Id, circuit.Id, mission.DroneId, sim.FindingCountForNextFlight(Random.Shared)), ct);
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
        string? status, string? missionId, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var catalog = await resources.GetAsync(ct);
        var all = await ListSettledFindingsAsync(repo, now, ct);
        var hours = new FieldHours(FieldWork.History(all), now);
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
            .Select(f => ToView(f, circuits, now, catalog, hours)));
    }

    private static async Task<Results<Ok<FindingView>, NotFound>> GetFinding(
        string id, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await ListSettledFindingsAsync(repo, now, ct);
        return (all.FirstOrDefault(x => x.Id == id) ?? await GetSettledFindingAsync(repo, id, now, ct)) is { } f
            ? TypedResults.Ok(ToView(f, circuits, now, await resources.GetAsync(ct), new FieldHours(FieldWork.History(all), now)))
            : TypedResults.NotFound();
    }

    /// <summary>Synthetic drone capture of the finding (SVG). A real deployment would serve the uploaded photo from S3.</summary>
    private static async Task<Results<ContentHttpResult, NotFound>> GetFindingImage(string id, IGridRepository repo, CancellationToken ct) =>
        await repo.GetFindingAsync(id, ct) is { } f ? TypedResults.Content(FindingImage.Svg(f), "image/svg+xml") : TypedResults.NotFound();

    /// <summary>
    /// Any signed-in user can acknowledge a flight alert (one, by its time and kind, or all on the mission).
    /// Telemetry keeps updating an in-flight mission, so a version conflict is retried on fresh data.
    /// </summary>
    private static async Task<Results<Ok<MissionView>, NotFound>> AcknowledgeAlerts(
        string id, AlertAckRequest? req, HttpContext ctx, IGridRepository repo, ICircuitCatalog circuits, TimeProvider clock, CancellationToken ct)
    {
        var user = User(ctx);
        for (var attempt = 1; ; attempt++)
        {
            var m = await repo.GetMissionAsync(id, ct);
            if (m is null) return TypedResults.NotFound();
            if (m.AcknowledgeAlerts(req?.At, req?.Kind, user.DisplayName, clock.GetUtcNow()) == 0) return TypedResults.Ok(ToView(m, circuits));
            try
            {
                await repo.SaveMissionAsync(m, ct);
                return TypedResults.Ok(ToView(m, circuits));
            }
            catch (ConcurrencyException) when (attempt < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt + Random.Shared.Next(20)), ct);
            }
        }
    }

    private static async Task<NoContent> RecordVisit(VisitRequest? req, HttpContext ctx, IVisitStore visits, TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var headers = ctx.Request.Headers;
        var ua = headers.UserAgent.ToString();
        if (req is null || VisitAnonymizer.IsBot(ua)) return TypedResults.NoContent();
        try
        {
            var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            // Behind CloudFront the client's address is the first X-Forwarded-For entry. It is only ever hashed.
            var ip = headers["X-Forwarded-For"].ToString().Split(',')[0].Trim() is { Length: > 0 } fwd ? fwd : ctx.Connection.RemoteIpAddress?.ToString();
            var key = VisitAnonymizer.VisitorKey(await visits.GetDailySaltAsync(day, ct), ip, ua);
            await visits.RecordAsync(VisitAnonymizer.Describe(day, key, req.Page, req.Referrer, ctx.Request.Host.Host, ua,
                headers["CloudFront-Viewer-Country"].ToString(), headers.AcceptLanguage.ToString(), req.SignedIn), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Analytics must never break the app.
            logs.CreateLogger("Visits").LogWarning(ex, "Could not record a page view");
        }
        return TypedResults.NoContent();
    }

    /// <summary>Supervisor-only visitor summary for the last <paramref name="days"/> UTC days (default 30, max 90).</summary>
    private static async Task<Results<Ok<VisitorStatsView>, ProblemHttpResult>> GetVisitorStats(
        int? days, HttpContext ctx, IVisitStore visits, TimeProvider clock, CancellationToken ct)
    {
        if (!User(ctx).IsSupervisor)
            return TypedResults.Problem("Only supervisors can see visitor stats.", statusCode: StatusCodes.Status403Forbidden);

        var n = Math.Clamp(days ?? 30, 1, 90);
        var to = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var list = await visits.GetDaysAsync(to.AddDays(1 - n), to, ct);

        static IReadOnlyList<CountView> Top(IEnumerable<IReadOnlyDictionary<string, int>> dims, int take = 10) => dims
            .SelectMany(d => d)
            .GroupBy(kv => kv.Key, kv => kv.Value)
            .Select(g => new CountView(g.Key, g.Sum()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(take).ToList();

        return TypedResults.Ok(new VisitorStatsView(n,
            list.Sum(d => d.PageViews), list.Sum(d => d.UniqueVisitors), list.Sum(d => d.SignedInVisitors),
            list.Select(d => new DayVisitsView(d.Day, d.PageViews, d.UniqueVisitors)).ToList(),
            Top(list.Select(d => d.Pages)), Top(list.Select(d => d.Referrers)), Top(list.Select(d => d.Devices)),
            Top(list.Select(d => d.Browsers)), Top(list.Select(d => d.OperatingSystems)), Top(list.Select(d => d.Countries)),
            Top(list.Select(d => d.Languages))));
    }

    /// <summary>The crew roster with each crew's open work orders (assigned, not yet resolved), crew-wide or to a member.</summary>
    private static async Task<Ok<IEnumerable<CrewView>>> ListCrews(IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var crews = (await resources.GetAsync(ct)).GroundCrews;
        var findings = await ListSettledFindingsAsync(repo, now, ct);
        var open = OpenWorkOrders(findings, crews);
        var hours = new FieldHours(FieldWork.History(findings), now);
        var active = ActiveOf(hours);
        return TypedResults.Ok(crews.Select(c => new CrewView(c, open.GetValueOrDefault(c.Id), [.. active.Where(d => d.CrewId == c.Id)], MemberShifts(c, hours))));
    }

    /// <summary>Open work orders (assigned, not yet resolved) per crew id, crew-wide or to a member.</summary>
    private static Dictionary<string, int> OpenWorkOrders(IEnumerable<Finding> findings, IReadOnlyList<Crew> crews) =>
        findings
            .Where(f => f.Status == FindingStatus.Assigned)
            .Select(f => CrewRoster.Resolve(crews, f.AssignedTo)?.Crew.Id)
            .Where(id => id is not null)
            .CountBy(id => id!)
            .ToDictionary();

    private static async Task<Results<Ok<FindingView>, NotFound, ValidationProblem, Conflict<string>, ProblemHttpResult>> ChangeFindingStatus(
        string id, FindingStatusRequest req, HttpContext ctx, IGridRepository repo, IResourceStore resources, ICircuitCatalog circuits, IRouteSource routes,
        TimeProvider clock, CancellationToken ct)
    {
        var user = User(ctx);
        var catalog = await resources.GetAsync(ct);
        var crews = catalog.GroundCrews;
        if (Finding.RequiresSupervisor(req.Status) && !user.IsSupervisor)
            return TypedResults.Problem($"Only supervisors can move a finding to {req.Status}.", statusCode: StatusCodes.Status403Forbidden);

        var now = clock.GetUtcNow();
        var all = await ListSettledFindingsAsync(repo, now, ct);
        var f = all.FirstOrDefault(x => x.Id == id) ?? await GetSettledFindingAsync(repo, id, now, ct);
        if (f is null) return TypedResults.NotFound();
        var hours = new FieldHours(FieldWork.History(all), now, f.Severity);
        var active = ActiveOf(hours);

        // Work orders go to someone on the crew roster, stored under their canonical label.
        var assignee = req.Assignee;
        FieldDispatch? dispatch = null;
        Finding? previous = null;
        if (req.Status == FindingStatus.Assigned && !string.IsNullOrWhiteSpace(assignee))
        {
            if (CrewRoster.Resolve(crews, assignee) is not { } who)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assignee"] = [$"'{assignee.Trim()}' isn't on the crew roster."] });
            if (who.Crew.Status == CrewStatus.OffDuty)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assignee"] = [$"{who.Crew.Name} is off duty."] });
            if (!who.Crew.CanFix(f.Type, who.Member))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignee"] = [$"{who.Label} isn't qualified to fix {FindingImage.Label(f.Type).ToLowerInvariant()}."],
                });
            // Someone already out can take it after their current jobs if it's nearby; otherwise they're busy.
            var candidate = CrewRoster.Candidates(crews, catalog.Depots, f.Type, f.Location, active, hours).FirstOrDefault(c => c.Label == who.Label);
            if (candidate is { BusyUntil: { } back })
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignee"] = [$"{who.Label} is out on a job elsewhere for about {Math.Max(1, (int)Math.Ceiling((back - now).TotalMinutes))} more minute(s)."],
                });
            // Hours of service: nobody works more than a shift's worth of driving and repairs without a rest.
            if (candidate is { Shift.RestingUntil: { } rested })
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignee"] = [$"{who.Label} has worked a full shift and is resting; back in about {Math.Max(1, (int)Math.Ceiling((rested - now).TotalMinutes))} minute(s)."],
                });
            if (candidate is { OutOfHours: true, Shift: { } shift, JobTime: { } job })
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["assignee"] = [$"{who.Label} has {Shifts.Hours(shift.Left):0.#} h of their {Shifts.LimitHours:0} h shift left; this job needs about {Shifts.Hours(job):0.#} h."],
                });
            assignee = who.Label;

            // Send them: a simulated truck roll along the roads, from their depot or straight on from their last job.
            previous = candidate?.After is { } after ? all.FirstOrDefault(x => ReferenceEquals(x.Dispatch, after)) : null;
            dispatch = candidate is null ? null : await FieldWork.PlanAsync(candidate, f, previous, routes, now, ct);

            // The estimate went by straight-line distance; check the real routes still fit the shift.
            if (dispatch is not null)
            {
                var planned = new FieldHours([.. all.Where(x => x.Id != f.Id).Select(x => x.Dispatch).OfType<FieldDispatch>(), dispatch], now);
                IEnumerable<CrewMember> members = who.Member is { } one ? [one] : who.Crew.Members;
                if (members.Select(m => planned.Of(who.Crew, m)).FirstOrDefault(s => s.Booked > Shifts.Limit) is { } over)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["assignee"] = [$"By road this job would take {who.Label} to {Shifts.Hours(over.Booked):0.#} h, over the {Shifts.LimitHours:0} h shift limit."],
                    });
            }
        }
        try
        {
            if (req.Status == FindingStatus.Resolved) await FieldWork.RecallAsync(f, routes, now, ct); // closed by hand before the repair finished
            f.MoveTo(req.Status, now, user.DisplayName, req.Note, assignee);
            if (dispatch is not null) f.Dispatch = dispatch;
            await repo.SaveFindingAsync(f, ct);
            if (previous is not null)
            {
                // The job before now hands over to this one instead of driving back to the depot.
                try { await repo.SaveFindingAsync(previous, ct); }
                catch (ConcurrencyException) { /* it was just settled; its drive back stays as planned */ }
            }
        }
        catch (InvalidTransitionException ex) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [ex.Message] }); }
        catch (ArgumentException ex) { return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["assignee"] = [ex.Message] }); }
        catch (ConcurrencyException ex) { return TypedResults.Conflict(ex.Message); }
        return TypedResults.Ok(ToView(f, circuits, now, catalog, new FieldHours(FieldWork.History(all.Select(x => x.Id == f.Id ? f : x)), now)));
    }

    /// <summary>Who this finding's work order can go to: qualified, on-duty technicians nearest first, then whole crews.</summary>
    private static async Task<Results<Ok<IEnumerable<AssigneeOption>>, NotFound>> ListAssignees(
        string id, IGridRepository repo, IResourceStore resources, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var all = await ListSettledFindingsAsync(repo, now, ct);
        if ((all.FirstOrDefault(x => x.Id == id) ?? await repo.GetFindingAsync(id, ct)) is not { } f) return TypedResults.NotFound();
        var catalog = await resources.GetAsync(ct);
        var open = OpenWorkOrders(all, catalog.GroundCrews);
        var hours = new FieldHours(FieldWork.History(all), now, f.Severity);
        return TypedResults.Ok(CrewRoster.Candidates(catalog.GroundCrews, catalog.Depots, f.Type, f.Location, ActiveOf(hours), hours).Select(c =>
            new AssigneeOption(c.Label, c.Crew.Id, c.Crew.Name, c.Member?.Name, c.Member?.Role, c.Depot?.Id, c.Depot?.Name, c.DistanceKm,
                open.GetValueOrDefault(c.Crew.Id), c.BusyUntil, c.After?.SiteLabel,
                c.Shift is { } s ? Shifts.Hours(s.Left) : null, c.JobTime is { } j ? Shifts.Hours(j) : null, c.Shift?.RestingUntil)));
    }
}
