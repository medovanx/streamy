using System.Globalization;
using System.Text.RegularExpressions;

namespace Streamy.Core.Media;

/// <summary>
/// Everything MediaStore knows about one item, read with a query that asks for all columns (older Android
/// versions lack some, and naming a missing column would fail the whole query). Shown in the gallery's
/// details panel: camera settings, orientation, favourite, the app that made it.
/// </summary>
public sealed partial record MediaDetails(IReadOnlyDictionary<string, string> Fields)
{
    public static string QueryCommand(MediaItem item) =>
        $"content query --uri content://media/external/{(item.IsVideo ? "video" : "images")}/media --where '_id={item.Id.ToString(CultureInfo.InvariantCulture)}'";

    public static MediaDetails Parse(string output)
    {
        var row = output.Replace("\r", "").Split('\n').FirstOrDefault(l => l.StartsWith("Row: ", StringComparison.Ordinal)) ?? "";
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = KeyRegex().Matches(row);
        for (int i = 0; i < keys.Count; i++)
        {
            int start = keys[i].Index + keys[i].Length;
            int end = i + 1 < keys.Count ? keys[i + 1].Index : row.Length;
            string value = row[start..end];
            if (value != "NULL") fields[keys[i].Groups[1].Value] = value;
        }
        return new MediaDetails(fields);
    }

    private string? Get(string key) => Fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private double? Number(string key) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>"f/1.8 · 1/4040 s · ISO 40", or null when the camera left no settings.</summary>
    public string? CameraSettings
    {
        get
        {
            var parts = new List<string>();
            if (Number("f_number") is > 0 and var f) parts.Add("f/" + f.ToString("0.#", CultureInfo.InvariantCulture));
            if (Number("exposure_time") is > 0 and var t) parts.Add(ExposureText(t));
            if (Number("iso") is > 0 and var iso) parts.Add("ISO " + iso.ToString("0", CultureInfo.InvariantCulture));
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>Shutter speed the way cameras show it: 1/4040 s for fast, 0.5 s or 2 s for slow.</summary>
    public static string ExposureText(double seconds) =>
        seconds >= 0.3
            ? seconds.ToString(seconds >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture) + " s"
            : "1/" + Math.Round(1 / seconds).ToString("0", CultureInfo.InvariantCulture) + " s";

    public int? Orientation => int.TryParse(Get("orientation"), out var o) && o != 0 ? o : null;

    public bool IsFavorite => Get("is_favorite") == "1";

    /// <summary>When it arrived on the phone (download, transfer, capture).</summary>
    public DateTimeOffset? Added => long.TryParse(Get("date_added"), out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>The app that made the file, in words where it is a well-known one.</summary>
    public string? CreatedBy
    {
        get
        {
            if (Get("owner_package_name") is not { } pkg) return null;
            if (pkg.Contains("camera", StringComparison.OrdinalIgnoreCase)) return "Camera";
            if (pkg.Contains("screenshot", StringComparison.OrdinalIgnoreCase) || pkg == "com.android.systemui") return "Screenshot";
            if (pkg.Contains("whatsapp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
            if (pkg.Contains("telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
            if (pkg.Contains("instagram", StringComparison.OrdinalIgnoreCase)) return "Instagram";
            if (pkg.Contains("facebook", StringComparison.OrdinalIgnoreCase)) return "Facebook";
            if (pkg.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
            return pkg;
        }
    }

    /// <summary>Video bit rate as "12.4 Mbit/s", when the phone recorded it.</summary>
    public string? BitRate => Number("bitrate") is > 0 and var b ? (b / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + " Mbit/s" : null;

    /// <summary>Video frame rate as "30 fps", when the phone recorded it.</summary>
    public string? FrameRate => Number("capture_framerate") is > 0 and var r ? r.ToString("0.##", CultureInfo.InvariantCulture) + " fps" : null;

    // "Row: 0 key=value, key=value": a key is a lower-case column name followed by "=".
    [GeneratedRegex(@"(?:^Row: \d+ |, )([a-z_0-9]+)=")]
    private static partial Regex KeyRegex();
}
