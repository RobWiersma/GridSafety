using System.Text.Json;
using System.Xml.Linq;
using GridSafety.Core;
using GridSafety.Functions;

namespace GridSafety.Tests;

/// <summary>Stands in for USGS: a tiny valid-looking JPEG, so no test touches the network.</summary>
public sealed class FakeAerialSource : IAerialSource
{
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[2000], 0xFF, 0xD9];
    public int Calls;
    public bool Fail;
    /// <summary>Fail this many calls first (like a USGS 502), then succeed.</summary>
    public int FailFirst;

    public Task<byte[]?> FetchAsync(GeoPoint center, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref Calls);
        return Fail || n <= FailFirst ? throw new HttpRequestException("502 Bad Gateway") : Task.FromResult<byte[]?>(Jpeg);
    }
}

public class AerialImageryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 18, 0, 0, TimeSpan.Zero);

    private static Finding Make(FindingType type, int severity = 4) => new()
    {
        Id = "f-aerial",
        MissionId = "m1",
        CircuitId = "C1",
        StructureId = "C1-S7",
        StructureLabel = "LM-007",
        Type = type,
        Severity = severity,
        Confidence = 0.81,
        Location = new GeoPoint(37.91984, -122.02448),
        Description = "test",
        CreatedAt = Now,
    };

    public static TheoryData<FindingType> Types() => [.. Enum.GetValues<FindingType>()];

    [Theory]
    [MemberData(nameof(Types))]
    public void Aerial_frame_embeds_the_photo_with_detection_hud_credit_and_badge(FindingType type)
    {
        var svg = FindingImage.AerialSvg(Make(type), FakeAerialSource.Jpeg);

        XDocument.Parse(svg); // well-formed
        Assert.Contains("data:image/jpeg;base64,", svg);
        Assert.Contains("SIMULATED", svg);
        Assert.Contains("NADIR", svg);
        Assert.Contains("USDA NAIP", svg);
        Assert.Contains(FindingImage.Label(type), svg);
        Assert.Contains("20 m", svg);
        Assert.Equal(type == FindingType.ThermalHotspot, svg.Contains("filter=\"url(#ir)\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Frame_is_centred_on_the_structure_and_about_160_by_100_metres()
    {
        var c = new GeoPoint(37.9, -122.0);
        var (w, s, e, n) = AerialFrame.BoundingBox(c);
        Assert.Equal(c.Lat, (s + n) / 2, 9);
        Assert.Equal(c.Lon, (w + e) / 2, 9);
        Assert.InRange(Geo.DistanceMeters(new GeoPoint(c.Lat, w), new GeoPoint(c.Lat, e)), 158, 162);
        Assert.InRange(Geo.DistanceMeters(new GeoPoint(s, c.Lon), new GeoPoint(n, c.Lon)), 98, 102);

        var url = UsgsNaipSource.Url(c).ToString();
        Assert.StartsWith("https://imagery.nationalmap.gov/", url);
        Assert.Contains("size=640,400", url);
        Assert.Contains("bboxSR=4326", url);
    }

    [Fact]
    public async Task A_structure_is_claimed_once_and_failures_wait_before_retrying()
    {
        var store = new InMemoryAerialStore();
        Assert.True(await store.TryClaimAsync("S1", Now));
        Assert.False(await store.TryClaimAsync("S1", Now.AddSeconds(30)));                  // being fetched
        Assert.True(await store.TryClaimAsync("S1", Now + AerialPolicy.PendingFor + TimeSpan.FromSeconds(1))); // that fetch died

        await store.MarkFailedAsync("S1", Now);
        Assert.Equal(AerialStatus.Failed, AerialPolicy.Effective(await store.GetAsync("S1"), Now.AddMinutes(10)));
        Assert.False(await store.TryClaimAsync("S1", Now.AddMinutes(10)));
        Assert.True(await store.TryClaimAsync("S1", Now + AerialPolicy.RetryFailedAfter + TimeSpan.FromMinutes(1)));

        await store.SaveAsync("S1", FakeAerialSource.Jpeg, Now);
        Assert.False(await store.TryClaimAsync("S1", Now.AddDays(30))); // cached for good
    }

    [Fact]
    public async Task A_failed_fetch_is_retried_then_recorded_not_thrown()
    {
        var store = new InMemoryAerialStore();
        var source = new FakeAerialSource { Fail = true };
        var errors = 0;
        var status = await AerialPolicy.FetchAsync(new AerialRequest("S2", new GeoPoint(37.9, -122)), source, store, TimeProvider.System,
            onError: (_, _) => errors++, retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);
        Assert.Equal(AerialStatus.Failed, status);
        Assert.Equal(3, source.Calls);
        Assert.Equal(3, errors);
        Assert.Equal(AerialStatus.Failed, (await store.GetAsync("S2")).Status);
    }

    [Fact]
    public async Task A_brief_USGS_error_is_retried_and_the_photo_still_arrives()
    {
        var store = new InMemoryAerialStore();
        var source = new FakeAerialSource { FailFirst = 1 };
        var status = await AerialPolicy.FetchAsync(new AerialRequest("S3", new GeoPoint(37.9, -122)), source, store, TimeProvider.System,
            retryDelays: [TimeSpan.Zero, TimeSpan.Zero]);
        Assert.Equal(AerialStatus.Ready, status);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task The_lambda_prefetches_on_FindingCreated_and_skips_cached_structures()
    {
        var repo = new InMemoryGridRepository();
        var finding = Make(FindingType.CrossarmDamage);
        await repo.SaveFindingAsync(finding);
        var store = new InMemoryAerialStore();
        var source = new FakeAerialSource();
        var fn = new AerialImageryFunction(repo, source, store, TimeProvider.System);
        var evt = JsonDocument.Parse($$$"""{"detail-type":"FindingCreated","detail":{"detailType":"FindingCreated","entityType":"Finding","entityId":"{{{finding.Id}}}","summary":"x"}}""");

        await fn.ProcessAsync(evt, CancellationToken.None);
        await fn.ProcessAsync(evt, CancellationToken.None);

        Assert.Equal(1, source.Calls);
        Assert.Equal(AerialStatus.Ready, (await store.GetAsync(finding.StructureId)).Status);
    }

    [Fact]
    public void Report_uses_the_aerial_photo_where_cached()
    {
        var c = StaticCircuitCatalog.Instance.All[1];
        var m = Mission.Plan(c, "DRN-9", "Pat", null, Now.AddMinutes(-30));
        var f = WorkflowTests.NewFinding(4);
        var html = MissionReportGenerator.Render(m, c, [], [f], Now, new Dictionary<string, byte[]> { [f.StructureId] = FakeAerialSource.Jpeg });
        var withPhoto = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            html.Split("data:image/svg+xml;base64,")[1].Split('"')[0]));
        Assert.Contains("data:image/jpeg;base64,", withPhoto);
        Assert.Contains("NAIP", html);
    }
}
