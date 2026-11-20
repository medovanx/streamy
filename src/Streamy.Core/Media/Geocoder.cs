using System.Globalization;

namespace Streamy.Core.Media;

/// <summary>
/// Offline reverse geocoding: the nearest city of 15,000+ people from GeoNames (CC BY 4.0), with its country.
/// Nothing leaves the PC. Cities are bucketed by whole degrees so a lookup checks a few hundred, not 30,000.
/// </summary>
public sealed class Geocoder
{
    private const double MaxKm = 75; // farther than this from any listed city: name the country only

    private readonly Dictionary<(int, int), List<City>> _grid = [];
    private readonly Dictionary<string, string> _countries;

    private sealed record City(string Name, string Country, GeoPoint Point);

    /// <param name="cities">Lines "name\tcountryCode\tlatitude\tlongitude".</param>
    /// <param name="countries">Lines "code\tname".</param>
    public Geocoder(IEnumerable<string> cities, IEnumerable<string> countries)
    {
        _countries = countries.Select(l => l.Split('\t')).Where(p => p.Length >= 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        foreach (var line in cities)
        {
            var p = line.Split('\t');
            if (p.Length < 4) continue;
            if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            var city = new City(p[0], p[1], new GeoPoint(lat, lon));
            var key = Cell(city.Point);
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = [];
            list.Add(city);
        }
    }

    public int CityCount => _grid.Values.Sum(l => l.Count);

    private static readonly Lazy<Geocoder> Shared = new(LoadEmbedded);

    /// <summary>The built-in city list (GeoNames cities15000, about 34,000 places), loaded once.</summary>
    public static Geocoder Default => Shared.Value;

    private static Geocoder LoadEmbedded()
    {
        var asm = typeof(Geocoder).Assembly;
        using var gz = asm.GetManifestResourceStream("geo/cities.tsv.gz") ?? throw new InvalidOperationException("City list missing.");
        using var unzip = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionMode.Decompress);
        using var cityReader = new StreamReader(unzip);
        using var cc = asm.GetManifestResourceStream("geo/countries.tsv") ?? throw new InvalidOperationException("Country list missing.");
        using var countryReader = new StreamReader(cc);
        return new Geocoder(ReadLines(cityReader).ToList(), ReadLines(countryReader).ToList());

        static IEnumerable<string> ReadLines(StreamReader r)
        {
            while (r.ReadLine() is { } line) yield return line;
        }
    }

    private static (int, int) Cell(GeoPoint p) => ((int)Math.Floor(p.Latitude), (int)Math.Floor(p.Longitude));

    /// <summary>"Alexandria, Egypt", or just the country when no city is near, or null for open sea.</summary>
    public string? Name(GeoPoint p)
    {
        City? best = null;
        double bestKm = double.MaxValue;
        var (cy, cx) = Cell(p);
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx;
                if (x < -180) x += 360; else if (x >= 180) x -= 360;
                if (!_grid.TryGetValue((cy + dy, x), out var list)) continue;
                foreach (var c in list)
                {
                    double km = p.DistanceKm(c.Point);
                    if (km < bestKm) { bestKm = km; best = c; }
                }
            }
        }
        if (best is null) return null;
        string country = _countries.GetValueOrDefault(best.Country, best.Country);
        return bestKm <= MaxKm ? $"{best.Name}, {country}" : country;
    }
}
