using System.Globalization;
using System.Text.Json;

namespace GridSafety.Core;

/// <summary>A drivable path between two points: the road geometry and its length.</summary>
public sealed record RoadRoute(IReadOnlyList<GeoPoint> Path, double Meters);

/// <summary>Finds the road route between two points; null when no route could be found (callers fall back to a straight line).</summary>
public interface IRouteSource
{
    Task<RoadRoute?> RouteAsync(GeoPoint from, GeoPoint to, CancellationToken ct = default);
}

/// <summary>
/// Road routing from OSRM (Open Source Routing Machine) over OpenStreetMap roads, the same open data the circuits come
/// from. Called only when a work order is assigned, never on a page view, and it gets only depot and structure coordinates.
/// The public demo server is fine for a portfolio demo's handful of requests; a real deployment would run its own.
/// </summary>
public sealed class OsrmRouteSource(HttpClient http) : IRouteSource
{
    public const string DefaultBaseUrl = "https://router.project-osrm.org";

    /// <summary>Paths are stored with the work order, so keep them small.</summary>
    public const int MaxPoints = 400;

    public async Task<RoadRoute?> RouteAsync(GeoPoint from, GeoPoint to, CancellationToken ct = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"/route/v1/driving/{from.Lon:F6},{from.Lat:F6};{to.Lon:F6},{to.Lat:F6}?overview=full&geometries=geojson");
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;
        return Parse(await res.Content.ReadAsStringAsync(ct), from, to);
    }

    /// <summary>
    /// Reads an OSRM route response. The path starts and ends exactly at the requested points: depots and towers are
    /// often off the road, so the first and last stretch is the walk (or dirt track) to the nearest road.
    /// </summary>
    public static RoadRoute? Parse(string json, GeoPoint from, GeoPoint to)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("code").GetString() != "Ok" || root.GetProperty("routes").GetArrayLength() == 0) return null;
        var route = root.GetProperty("routes")[0];
        var coords = route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
            .Select(c => new GeoPoint(c[1].GetDouble(), c[0].GetDouble()))
            .ToList();
        if (coords.Count == 0) return null;
        List<GeoPoint> path = [from, .. coords, to];
        return new RoadRoute(FieldPaths.Thin(path, MaxPoints), FieldPaths.Length(path));
    }
}

/// <summary>Never finds a road: everything drives in a straight line. Used when routing is turned off.</summary>
public sealed class StraightLineRouteSource : IRouteSource
{
    public static readonly StraightLineRouteSource Instance = new();
    public Task<RoadRoute?> RouteAsync(GeoPoint from, GeoPoint to, CancellationToken ct = default) => Task.FromResult<RoadRoute?>(null);
}

/// <summary>Helpers for paths stored as lists of points.</summary>
public static class FieldPaths
{
    public static double Length(IReadOnlyList<GeoPoint> path)
    {
        var m = 0.0;
        for (var i = 1; i < path.Count; i++) m += Geo.DistanceMeters(path[i - 1], path[i]);
        return m;
    }

    /// <summary>The point <paramref name="fraction"/> of the way along the path, by distance.</summary>
    public static GeoPoint Along(IReadOnlyList<GeoPoint> path, double fraction) => Split(path, fraction).At;

    /// <summary>The path up to <paramref name="fraction"/> of the way along it, ending at that point.</summary>
    public static IReadOnlyList<GeoPoint> Prefix(IReadOnlyList<GeoPoint> path, double fraction)
    {
        var (at, index) = Split(path, fraction);
        return [.. path.Take(index + 1), at];
    }

    private static (GeoPoint At, int Index) Split(IReadOnlyList<GeoPoint> path, double fraction)
    {
        if (path.Count == 0) throw new ArgumentException("Empty path.", nameof(path));
        if (path.Count == 1 || fraction <= 0) return (path[0], 0);
        var target = Length(path) * Math.Min(1, fraction);
        var walked = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var leg = Geo.DistanceMeters(path[i - 1], path[i]);
            if (walked + leg >= target)
            {
                var k = leg == 0 ? 0 : (target - walked) / leg;
                var (a, b) = (path[i - 1], path[i]);
                return (new GeoPoint(a.Lat + (b.Lat - a.Lat) * k, a.Lon + (b.Lon - a.Lon) * k), i - 1);
            }
            walked += leg;
        }
        return (path[^1], path.Count - 1);
    }

    /// <summary>Keeps at most <paramref name="max"/> points, evenly spaced, always keeping the ends.</summary>
    public static IReadOnlyList<GeoPoint> Thin(IReadOnlyList<GeoPoint> path, int max)
    {
        if (path.Count <= max) return path;
        var step = (path.Count - 1) / (double)(max - 1);
        return [.. Enumerable.Range(0, max).Select(i => path[(int)Math.Round(i * step)])];
    }
}
