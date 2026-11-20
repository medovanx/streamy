namespace Streamy.Core.Media;

/// <summary>Web Mercator, as map tiles use it: world pixel coordinates at a zoom level, and back.</summary>
public static class MapMath
{
    public const double MaxLatitude = 85.05112878;

    /// <summary>World pixel position of a point at <paramref name="zoom"/>, for tiles <paramref name="tile"/> pixels wide.</summary>
    public static (double X, double Y) ToPixel(GeoPoint p, double zoom, int tile = 256)
    {
        double size = tile * Math.Pow(2, zoom);
        double lat = Math.Clamp(p.Latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        double x = (p.Longitude + 180) / 360 * size;
        double y = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * size;
        return (x, y);
    }

    public static GeoPoint FromPixel(double x, double y, double zoom, int tile = 256)
    {
        double size = tile * Math.Pow(2, zoom);
        double lon = x / size * 360 - 180;
        double n = Math.PI - 2 * Math.PI * y / size;
        double lat = 180 / Math.PI * Math.Atan(Math.Sinh(n));
        return new GeoPoint(lat, lon);
    }

    /// <summary>The largest whole zoom at which all points fit in a view of the given size (with a margin).</summary>
    public static int FitZoom(IReadOnlyCollection<GeoPoint> points, int width, int height, int maxZoom = 16, int tile = 256)
    {
        if (points.Count == 0) return 2;
        for (int z = maxZoom; z > 1; z--)
        {
            var px = points.Select(p => ToPixel(p, z, tile)).ToList();
            double w = px.Max(p => p.X) - px.Min(p => p.X), h = px.Max(p => p.Y) - px.Min(p => p.Y);
            if (w <= width * 0.85 && h <= height * 0.85) return z;
        }
        return 1;
    }
}
