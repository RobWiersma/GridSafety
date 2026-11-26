using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using AWS.Lambda.Powertools.Logging;
using AWS.Lambda.Powertools.Tracing;
using GridSafety.Core;

namespace GridSafety.Functions;

/// <summary>When a mission completes or aborts, render a self-contained HTML inspection report to S3.</summary>
public sealed class MissionReportGenerator(
    IGridRepository repo, ICircuitCatalog circuits, IAmazonS3 s3, string bucket, TimeProvider clock, IAerialStore? aerials = null)
{
    public MissionReportGenerator() : this(
        Repo.FromEnvironment(), StaticCircuitCatalog.Instance, new AmazonS3Client(), Env.Required("REPORTS_BUCKET"), TimeProvider.System,
        new DynamoAerialStore(new Amazon.DynamoDBv2.AmazonDynamoDBClient(), Env.Required("TABLE_NAME")))
    { }

    [Logging(LogEvent = false)]
    [Tracing(CaptureMode = TracingCaptureMode.Error)]
    public async Task HandleAsync(CloudWatchEvent<JsonElement> evt, ILambdaContext context)
    {
        var e = evt.Detail.Deserialize<GridEvent>(Json.Options) ?? throw new InvalidOperationException("Empty event detail");
        Traced.Annotate(e);
        var mission = await repo.GetMissionAsync(e.EntityId) ?? throw new NotFoundException($"Mission {e.EntityId}");
        var circuit = circuits.Find(mission.CircuitId) ?? throw new NotFoundException($"Circuit {mission.CircuitId}");
        var track = await repo.GetTrackAsync(mission.Id);
        var findings = (await repo.ListFindingsAsync(1000)).Where(f => f.MissionId == mission.Id).OrderByDescending(f => f.Severity).ToList();

        // Use the aerial photo where one is already cached; otherwise the illustrated close-up.
        var photos = new Dictionary<string, byte[]>();
        if (aerials is not null)
        {
            foreach (var id in findings.Select(f => f.StructureId).Distinct())
                if (await aerials.GetAsync(id) is { Status: AerialStatus.Ready, Jpeg: { } jpeg }) photos[id] = jpeg;
        }

        var key = $"reports/{mission.CreatedAt:yyyy/MM}/{mission.Id}.html";
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = "text/html; charset=utf-8",
            ContentBody = Render(mission, circuit, track, findings, clock.GetUtcNow(), photos),
        });

        for (var attempt = 1; ; attempt++)
        {
            var m = await repo.GetMissionAsync(mission.Id) ?? throw new NotFoundException($"Mission {mission.Id}");
            m.ReportKey = key;
            try { await repo.SaveMissionAsync(m); break; }
            catch (ConcurrencyException) when (attempt < 5) { await Task.Delay(50 * attempt); }
        }

        Logger.LogInformation("Report for {MissionId} written to s3://{Bucket}/{Key}", mission.Id, bucket, key);
    }

    public static string Render(
        Mission m, Circuit c, IReadOnlyList<TelemetryPoint> track, IReadOnlyList<Finding> findings, DateTimeOffset generatedAt,
        IReadOnlyDictionary<string, byte[]>? aerialPhotos = null)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var inv = CultureInfo.InvariantCulture;
        var duration = m.StartedAt is { } s && m.EndedAt is { } en ? en - s : TimeSpan.Zero;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Inspection report {{H(m.Id)}}</title>
            <style>
              body{font:14px/1.5 system-ui,sans-serif;margin:32px auto;max-width:960px;padding:0 16px;color:#111}
              h1{margin:0 0 4px}.muted{color:#666}table{border-collapse:collapse;width:100%;margin:12px 0 24px}
              th,td{border-bottom:1px solid #ddd;padding:6px 8px;text-align:left}th{background:#f5f5f5}
              .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px;margin:20px 0}
              .tile{border:1px solid #ddd;border-radius:8px;padding:10px 12px}.tile b{display:block;font-size:22px}
              .sev{display:inline-block;min-width:22px;text-align:center;border-radius:4px;color:#fff;font-weight:700}
              .s5{background:#b91c1c}.s4{background:#ea580c}.s3{background:#ca8a04}.s2{background:#2563eb}.s1{background:#64748b}
              svg{width:100%;height:auto;border:1px solid #ddd;border-radius:8px;background:#fafafa}
              .shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:12px;margin:12px 0 24px}
              figure{margin:0}figure img{width:100%;height:auto;border-radius:8px;display:block}figcaption{font-size:13px;margin-top:4px}
            </style></head><body>
            <h1>Inspection report: {{H(c.Name)}}</h1>
            <div class="muted">{{(m.Name is null ? "" : $"Flight {H(m.Name)} · ")}}Mission {{H(m.Id)}} · Drone {{H(m.DroneId)}} · PIC {{H(m.Pilot)}} · {{m.Status}} · Generated {{generatedAt:u}}</div>
            <div class="grid">
              <div class="tile">Coverage<b>{{m.Coverage(c) * 100:0}}%</b>{{m.StructuresInspected.Count}} / {{c.Stretch(m.Leg).Structures.Count}} structures{{(c.Leg(m.Leg) is { Of: > 1 } leg ? $" · flight {leg.Number} of {leg.Of} ({H(leg.FromLabel)} to {H(leg.ToLabel)})" : "")}}</div>
              <div class="tile">Distance<b>{{m.DistanceFlownMeters / 1609.344:0.0}} mi</b>{{(c.VoltageKv > 0 ? $"{c.VoltageKv} kV circuit" : "voltage not tagged")}}</div>
              <div class="tile">Flight time<b>{{(int)duration.TotalMinutes}}m {{duration.Seconds}}s</b>{{track.Count}} telemetry points</div>
              <div class="tile">Findings<b>{{findings.Count}}</b>{{findings.Count(f => f.Severity >= 4)}} high/critical</div>
              <div class="tile">Flight alerts<b>{{m.RecentAlerts.Count}}</b>{{m.RecentAlerts.Count(a => a.Level == AlertLevel.Critical)}} critical</div>
            </div>
            """);

        sb.Append(TrackSvg(c, track, findings, inv));

        sb.Append("<h2>Findings</h2>");
        if (findings.Count == 0) sb.Append("<p class=\"muted\">No issues reported on this patrol.</p>");
        else
        {
            sb.Append("<table><tr><th>Sev</th><th>Structure</th><th>Type</th><th>Confidence</th><th>Status</th><th>Notes</th></tr>");
            foreach (var f in findings)
            {
                sb.Append(inv, $"<tr><td><span class=\"sev s{f.Severity}\">{f.Severity}</span></td><td>{H(f.StructureLabel)}</td>" +
                               $"<td>{H(StreamPublisher.Humanize(f.Type))}</td><td>{f.Confidence:P0}</td><td>{f.Status}</td><td>{H(f.Description)}</td></tr>");
            }
            sb.Append("</table>");

            sb.Append("<h2>Captures</h2><p class=\"muted\">Simulated captures for this demo: the defects are generated, the aerial photos are public-domain USDA NAIP imagery of each structure's real location. A production drone would attach its own photos.</p><div class=\"shots\">");
            foreach (var f in findings)
            {
                var svg = aerialPhotos?.GetValueOrDefault(f.StructureId) is { } jpeg ? FindingImage.AerialSvg(f, jpeg) : FindingImage.Svg(f);
                var img = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
                sb.Append(inv, $"<figure><img src=\"data:image/svg+xml;base64,{img}\" alt=\"{H(StreamPublisher.Humanize(f.Type))} at {H(f.StructureLabel)}\">" +
                               $"<figcaption><span class=\"sev s{f.Severity}\">{f.Severity}</span> {H(f.StructureLabel)} · {H(f.Description)}</figcaption></figure>");
            }
            sb.Append("</div>");
        }

        if (m.RecentAlerts.Count > 0)
        {
            sb.Append("<h2>Flight alerts</h2><table><tr><th>Time (UTC)</th><th>Level</th><th>Alert</th></tr>");
            foreach (var a in m.RecentAlerts.OrderBy(a => a.At))
                sb.Append(inv, $"<tr><td>{a.At:HH:mm:ss}</td><td>{a.Level}</td><td>{H(a.Message)}</td></tr>");
            sb.Append("</table>");
        }

        var missed = c.Structures.Where(s => !m.StructuresInspected.Contains(s.Id)).Select(s => s.Label).ToList();
        if (missed.Count > 0)
            sb.Append(inv, $"<h2>Not inspected</h2><p>{H(string.Join(", ", missed))}. Schedule a follow-up patrol.</p>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Inline SVG of circuit, flight path and findings: no map tiles, so the report works offline and forever.</summary>
    private static string TrackSvg(Circuit c, IReadOnlyList<TelemetryPoint> track, IReadOnlyList<Finding> findings, CultureInfo inv)
    {
        var pts = c.Path.Concat(track.Select(t => t.Position)).ToList();
        double minLat = pts.Min(p => p.Lat), maxLat = pts.Max(p => p.Lat), minLon = pts.Min(p => p.Lon), maxLon = pts.Max(p => p.Lon);
        var lonScale = Math.Cos((minLat + maxLat) / 2 * Math.PI / 180);
        double w = 900, pad = 20;
        var spanX = Math.Max((maxLon - minLon) * lonScale, 1e-6);
        var spanY = Math.Max(maxLat - minLat, 1e-6);
        var scale = (w - 2 * pad) / spanX;
        var h = Math.Max(200, spanY * scale + 2 * pad);
        string P(GeoPoint p) => string.Create(inv, $"{pad + (p.Lon - minLon) * lonScale * scale:0.0},{h - pad - (p.Lat - minLat) * scale:0.0}");

        var sb = new StringBuilder();
        sb.Append(inv, $"<svg viewBox=\"0 0 {w:0} {h:0}\" role=\"img\" aria-label=\"Flight path\">");
        sb.Append(inv, $"<polyline points=\"{string.Join(' ', c.Path.Select(P))}\" fill=\"none\" stroke=\"#94a3b8\" stroke-width=\"6\" stroke-linecap=\"round\"/>");
        if (track.Count > 1)
            sb.Append(inv, $"<polyline points=\"{string.Join(' ', track.Select(t => P(t.Position)))}\" fill=\"none\" stroke=\"#2563eb\" stroke-width=\"2\"/>");
        foreach (var s in c.Structures)
            sb.Append(inv, $"<circle cx=\"{P(s.Location).Split(',')[0]}\" cy=\"{P(s.Location).Split(',')[1]}\" r=\"3\" fill=\"#475569\"/>");
        foreach (var f in findings)
        {
            var xy = P(f.Location).Split(',');
            var color = f.Severity >= 5 ? "#b91c1c" : f.Severity == 4 ? "#ea580c" : f.Severity == 3 ? "#ca8a04" : "#2563eb";
            sb.Append(inv, $"<circle cx=\"{xy[0]}\" cy=\"{xy[1]}\" r=\"8\" fill=\"{color}\" fill-opacity=\".85\" stroke=\"#fff\" stroke-width=\"2\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
