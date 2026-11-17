using System.Reflection;
using System.Text.RegularExpressions;
using GridSafety.Functions;

namespace GridSafety.Tests;

/// <summary>
/// The Lambda runtime finds a handler by type and method name and refuses to start if the name is overloaded
/// (it only fails in AWS, on the first invocation). Check every handler the stack wires up.
/// </summary>
public partial class LambdaHandlerTests
{
    [Fact]
    public void Every_configured_handler_exists_and_is_not_overloaded()
    {
        var stack = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "GridSafetyStack.cs"));
        var handlers = Handler().Matches(stack).Select(m => (Type: m.Groups[1].Value, Method: m.Groups[2].Value)).ToList();
        Assert.NotEmpty(handlers);

        var assembly = typeof(DroneIngestFunction).Assembly;
        foreach (var (typeName, method) in handlers)
        {
            var type = assembly.GetType($"GridSafety.Functions.{typeName}");
            Assert.True(type is not null, $"{typeName} not found");
            var candidates = type!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(m => m.Name == method).ToList();
            Assert.True(candidates.Count == 1, $"{typeName}::{method} has {candidates.Count} public overloads; Lambda needs exactly one");
        }
    }

    [GeneratedRegex(@"\{fn\}(\w+)::(\w+)""")]
    private static partial Regex Handler();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSafety.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found");
    }
}
