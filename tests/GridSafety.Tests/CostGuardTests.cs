using System.Reflection;
using System.Text.RegularExpressions;
using AWS.Lambda.Powertools.Metrics;
using GridSafety.Functions;

namespace GridSafety.Tests;

/// <summary>
/// CloudWatch bills $0.30/month for every custom metric series beyond the first 10. These tests keep the
/// demo inside that free allowance: no per-function ColdStart series, and a fixed list of business metrics.
/// </summary>
public partial class CostGuardTests
{
    private static readonly string[] AllowedMetrics = ["TelemetryPoints", "FindingsReported", "DroneAlerts", "MessagesRejected", "SlaEscalations"];

    [Fact]
    public void No_handler_captures_cold_start_metrics()
    {
        var handlers = typeof(DroneIngestFunction).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<MetricsAttribute>()))
            .Where(x => x.Attr is not null)
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, h => Assert.False(h.Attr!.CaptureColdStart, $"{h.Method.DeclaringType!.Name} captures ColdStart (one billed series per function)"));
    }

    [Fact]
    public void Custom_metrics_stay_on_the_allow_list()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var emitted = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => AddMetric().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(AllowedMetrics.Order().ToArray(), emitted);
        Assert.True(emitted.Length <= 10, "Over the CloudWatch free tier of 10 custom metrics");
    }

    [GeneratedRegex(@"AddMetric\(""([A-Za-z]+)""")]
    private static partial Regex AddMetric();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSafety.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}
