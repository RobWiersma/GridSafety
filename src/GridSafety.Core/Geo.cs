namespace GridSafety.Core;

public readonly record struct GeoPoint(double Lat, double Lon);

public static class Geo
{
    private const double EarthRadiusM = 6_371_000;

    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        var dLat = ToRad(b.Lat - a.Lat);
        var dLon = ToRad(b.Lon - a.Lon);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRad(a.Lat)) * Math.Cos(ToRad(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>
    /// Shortest distance from a point to a polyline (e.g. a distribution circuit).
    /// Uses a local equirectangular projection, accurate to well under a meter at corridor scale.
    /// </summary>
    public static double DistanceToPolylineMeters(GeoPoint p, IReadOnlyList<GeoPoint> line)
    {
        if (line.Count == 0) return double.PositiveInfinity;
        if (line.Count == 1) return DistanceMeters(p, line[0]);

        var best = double.PositiveInfinity;
        for (var i = 0; i < line.Count - 1; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, line[i], line[i + 1]));
        }
        return best;
    }

    /// <summary>Point a given fraction (0..1) of the way from a to b.</summary>
    public static GeoPoint Lerp(GeoPoint a, GeoPoint b, double t) =>
        new(a.Lat + (b.Lat - a.Lat) * t, a.Lon + (b.Lon - a.Lon) * t);

    /// <summary>Offsets a point by meters north/east.</summary>
    public static GeoPoint Offset(GeoPoint p, double northM, double eastM) =>
        new(p.Lat + northM / EarthRadiusM * 180 / Math.PI,
            p.Lon + eastM / (EarthRadiusM * Math.Cos(ToRad(p.Lat))) * 180 / Math.PI);

    private static double DistanceToSegment(GeoPoint p, GeoPoint a, GeoPoint b)
    {
        var k = Math.Cos(ToRad(p.Lat)) * EarthRadiusM * Math.PI / 180;
        const double m = EarthRadiusM * Math.PI / 180;
        double ax = a.Lon * k, ay = a.Lat * m, bx = b.Lon * k, by = b.Lat * m, px = p.Lon * k, py = p.Lat * m;
        double dx = bx - ax, dy = by - ay;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + t * dx - px, cy = ay + t * dy - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;
}
