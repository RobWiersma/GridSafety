using GridSafety.Core;

namespace GridSafety.Tests;

public class VisitTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);
    private const string ChromeWin = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0 Safari/537.36";
    private const string SafariIphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    [Theory]
    [InlineData(null, "direct")]
    [InlineData("", "direct")]
    [InlineData("https://www.linkedin.com/feed/?trk=abc&user=42", "linkedin.com")]
    [InlineData("https://news.ycombinator.com/item?id=1", "news.ycombinator.com")]
    [InlineData("https://gridsafety.bswiersma.com/#ops", "direct")]
    [InlineData("android-app://com.slack/", "other")]
    [InlineData("not a url", "other")]
    public void Referrer_keeps_only_the_external_host(string? referrer, string expected) =>
        Assert.Equal(expected, VisitAnonymizer.ReferrerHost(referrer, "gridsafety.bswiersma.com"));

    [Fact]
    public void User_agent_reduces_to_coarse_families()
    {
        Assert.Equal(("Desktop", "Chrome", "Windows"), (VisitAnonymizer.Device(ChromeWin), VisitAnonymizer.Browser(ChromeWin), VisitAnonymizer.OperatingSystem(ChromeWin)));
        Assert.Equal(("Mobile", "Safari", "iOS"), (VisitAnonymizer.Device(SafariIphone), VisitAnonymizer.Browser(SafariIphone), VisitAnonymizer.OperatingSystem(SafariIphone)));
        Assert.True(VisitAnonymizer.IsBot("Mozilla/5.0 (compatible; Googlebot/2.1)"));
        Assert.True(VisitAnonymizer.IsBot(null));
        Assert.False(VisitAnonymizer.IsBot(ChromeWin));
    }

    [Theory]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData(null, "unknown")]
    [InlineData("<script>", "unknown")]
    public void Language_is_the_primary_tag_only(string? header, string expected) =>
        Assert.Equal(expected, VisitAnonymizer.Language(header));

    [Fact]
    public void Visitor_key_is_stable_within_a_day_and_unlinkable_across_salts()
    {
        var a = VisitAnonymizer.VisitorKey("salt-1", "203.0.113.7", ChromeWin);
        Assert.Equal(a, VisitAnonymizer.VisitorKey("salt-1", "203.0.113.7", ChromeWin));
        Assert.NotEqual(a, VisitAnonymizer.VisitorKey("salt-2", "203.0.113.7", ChromeWin));
        Assert.NotEqual(a, VisitAnonymizer.VisitorKey("salt-1", "203.0.113.8", ChromeWin));
        Assert.DoesNotContain("203", a);
        Assert.Equal(24, a.Length);
    }

    [Fact]
    public void Describe_drops_unknown_values()
    {
        var v = VisitAnonymizer.Describe(Today, "k", "../../admin", "https://evil.example/path", null, ChromeWin, "zz9", "en", false);
        Assert.Equal("other", v.Page);
        Assert.Equal("evil.example", v.Referrer);
        Assert.Equal("unknown", v.Country);
    }

    [Fact]
    public async Task Store_counts_unique_visitors_once_per_day_and_views_every_time_and_keeps_signed_in_views_apart()
    {
        var store = new InMemoryVisitStore();
        PageVisit View(string key, string page, bool signedIn = false) =>
            VisitAnonymizer.Describe(Today, key, page, "https://www.linkedin.com/", null, ChromeWin, "US", "en-US", signedIn);

        await store.RecordAsync(View("a", "ops"));
        await store.RecordAsync(View("a", "findings"));
        await store.RecordAsync(View("b", "ops", signedIn: true));
        await store.RecordAsync(View("c", "ops"));
        await store.RecordAsync(View("c", "mission", signedIn: true)); // signed in after arriving as a guest: still one visitor
        await store.RecordAsync(View("c", "mission", signedIn: true));
        await store.RecordAsync(View("a", "ops") with { Day = Today.AddDays(1) });

        var days = await store.GetDaysAsync(Today, Today.AddDays(1));
        var d = days[0];
        Assert.Equal((3, 2, 3), (d.PageViews, d.UniqueVisitors, d.SignedInViews));
        Assert.Equal(2, d.Pages["ops"]);
        Assert.Equal(1, d.Pages["findings"]);
        Assert.Equal(2, d.Referrers["linkedin.com"]); // per visitor, not per view
        Assert.Equal(2, d.Countries["US"]);
        Assert.Equal((1, 1), (days[1].PageViews, days[1].UniqueVisitors));
        Assert.Equal(await store.GetDailySaltAsync(Today), await store.GetDailySaltAsync(Today));
        Assert.NotEqual(await store.GetDailySaltAsync(Today), await store.GetDailySaltAsync(Today.AddDays(1)));
    }
}
