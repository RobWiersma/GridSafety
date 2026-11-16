using System.Security.Claims;
using System.Text.Json;
using Amazon.DynamoDBv2;
using GridSafety.Core;
using Microsoft.Extensions.Options;

namespace GridSafety.Api;

public sealed class ApiOptions
{
    public string? TableName { get; set; }
    public string? ReportsBucket { get; set; }
}

public static class Roles
{
    /// <summary>Cognito group whose members can dismiss findings and see supervisor tools.</summary>
    public const string SupervisorsGroup = "Supervisors";
}

public sealed record CurrentUser(string Id, string? Email, IReadOnlyList<string> Groups)
{
    public bool IsSupervisor => Groups.Contains(Roles.SupervisorsGroup, StringComparer.OrdinalIgnoreCase);
    public string DisplayName => Email ?? Id;
}

public static class UserResolver
{
    // Key the Lambda ASP.NET Core bridge uses to stash the raw API Gateway request.
    private const string LambdaRequestItem = "LambdaRequestObject";

    /// <summary>
    /// API Gateway's JWT authorizer has already validated the Cognito token. The bridge maps its
    /// claims onto HttpContext.User; we fall back to the raw request context just in case.
    /// </summary>
    public static CurrentUser? Resolve(HttpContext ctx)
    {
        var sub = ctx.User.FindFirstValue("sub") ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub is not null)
        {
            var groups = ctx.User.FindAll("cognito:groups").SelectMany(c => ParseGroups(c.Value)).ToList();
            return new CurrentUser(sub, ctx.User.FindFirstValue("email") ?? ctx.User.FindFirstValue(ClaimTypes.Email), groups);
        }

        if (ctx.Items.TryGetValue(LambdaRequestItem, out var raw) && raw is not null)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(raw));
            if (doc.RootElement.TryGetProperty("RequestContext", out var rc)
                && rc.TryGetProperty("Authorizer", out var auth)
                && auth.TryGetProperty("Jwt", out var jwt)
                && jwt.TryGetProperty("Claims", out var claims)
                && claims.TryGetProperty("sub", out var s))
            {
                return new CurrentUser(
                    s.GetString()!,
                    claims.TryGetProperty("email", out var e) ? e.GetString() : null,
                    claims.TryGetProperty("cognito:groups", out var g) ? ParseGroups(g.GetString() ?? "") : []);
            }
        }
        return null;
    }

    /// <summary>HTTP API flattens array claims to strings like "[Supervisors Pilots]".</summary>
    internal static IReadOnlyList<string> ParseGroups(string raw) =>
        raw.Trim('[', ']').Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class DevUserMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Dev-User", out var user) && !string.IsNullOrWhiteSpace(user))
        {
            var claims = new List<Claim> { new("sub", user!), new("email", $"{user}@gridsafety.dev") };
            if (ctx.Request.Headers.TryGetValue("X-Dev-Role", out var role) && role == "Supervisor")
                claims.Add(new Claim("cognito:groups", Roles.SupervisorsGroup));
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Dev"));
        }
        return next(ctx);
    }
}

/// <summary>Defers S3 client creation until an endpoint actually needs it.</summary>
public sealed class S3Presigner(Func<Amazon.S3.IAmazonS3> factory)
{
    private readonly Lazy<Amazon.S3.IAmazonS3> _client = new(factory);
    public Amazon.S3.IAmazonS3 Client => _client.Value;
}

internal sealed class LocalTableBootstrapper(IAmazonDynamoDB db, IOptions<ApiOptions> options, ILogger<LocalTableBootstrapper> log)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await DynamoGridRepository.EnsureTableAsync(db, options.Value.TableName!, ct);
        log.LogInformation("DynamoDB table {Table} is ready", options.Value.TableName);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
