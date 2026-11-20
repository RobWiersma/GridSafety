using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace GridSafety.Api;

/// <summary>
/// Redacts JSON for signed-out visitors: who planned, triaged or changed something (and any email address
/// anywhere) becomes "Ops user", and no actions are offered. The data itself (lines, flights, findings) is
/// simulated or public OpenStreetMap, so it stays.
/// </summary>
public static partial class GuestView
{
    public const string Redacted = "Ops user";

    /// <summary>Properties that name the person behind an action.</summary>
    private static readonly HashSet<string> PersonFields = new(StringComparer.OrdinalIgnoreCase) { "createdBy", "by", "acknowledgedBy" };

    /// <summary>Names the system itself uses in audit trails; these are not people.</summary>
    private static readonly HashSet<string> SystemActors = new(StringComparer.OrdinalIgnoreCase) { "system", "drone" };

    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var result = await next(ctx);
        var inner = result is INestedHttpResult nested ? nested.Result : result as IResult;
        if (inner is ContentHttpResult || inner is not IValueHttpResult { Value: { } value }) return result; // images, 404s

        var options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
        Redact(node);
        return Results.Json(node, options, statusCode: (inner as IStatusCodeHttpResult)?.StatusCode);
    }

    public static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (key.Equals("nextStatuses", StringComparison.OrdinalIgnoreCase)) obj[key] = new JsonArray();
                    else if (key.Equals("payRate", StringComparison.OrdinalIgnoreCase) || key.Equals("payHistory", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("profile", StringComparison.OrdinalIgnoreCase))
                        obj.Remove(key); // pay and employee records are for signed-in users
                    else if (child is JsonValue v && v.TryGetValue<string>(out var s))
                        obj[key] = PersonFields.Contains(key) && !SystemActors.Contains(s) ? Redacted : Scrub(s);
                    else Redact(child);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)) arr[i] = Scrub(s);
                    else Redact(arr[i]);
                }
                break;
        }
    }

    private static string Scrub(string s) => Email().Replace(s, Redacted);

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}
