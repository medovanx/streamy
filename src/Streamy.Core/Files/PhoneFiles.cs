using System.Globalization;
using System.Text;

namespace Streamy.Core.Files;

/// <summary>One file or folder on the phone.</summary>
public sealed record PhoneFile(string Name, bool IsDirectory, long Size, DateTimeOffset Modified)
{
    /// <summary>Lower-case extension without the dot, or "" (".nomedia" counts as having none).</summary>
    public string Extension
    {
        get
        {
            int dot = Name.LastIndexOf('.');
            return dot <= 0 || IsDirectory ? "" : Name[(dot + 1)..].ToLowerInvariant();
        }
    }
}

/// <summary>Listing, paths and shell quoting for browsing the phone's storage over adb.</summary>
public static class PhoneFiles
{
    public const string Home = "/sdcard";

    /// <summary>Folders worth one click: (label, path).</summary>
    public static readonly IReadOnlyList<(string Label, string Path)> Places =
    [
        ("Internal storage", "/sdcard"),
        ("Download", "/sdcard/Download"),
        ("Camera", "/sdcard/DCIM/Camera"),
        ("Screenshots", "/sdcard/DCIM/Screenshots"),
        ("Pictures", "/sdcard/Pictures"),
        ("Movies", "/sdcard/Movies"),
        ("Music", "/sdcard/Music"),
        ("Documents", "/sdcard/Documents"),
    ];

    /// <summary>
    /// The shell command that lists a folder: one "type|size|mtime|name" line per entry, following symlinks so
    /// /sdcard and friends show as folders. One process for the whole folder. A folder that can be entered but not
    /// read (/data) would otherwise look empty, so that case says "Permission denied" on stderr like a failed cd.
    /// </summary>
    public static string ListCommand(string path) =>
        $"cd {Quote(path)} && {{ [ -r . ] || {{ echo 'Permission denied' >&2; exit 1; }}; stat -L -c '%F|%s|%Y|%n' -- * .* 2>/dev/null; true; }}";

    /// <summary>Parses <see cref="ListCommand"/> output, folders first, then by name.</summary>
    public static IReadOnlyList<PhoneFile> ParseListing(string output)
    {
        var list = new List<PhoneFile>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var parts = raw.Split('|', 4);
            if (parts.Length < 4) continue;
            string name = parts[3];
            if (name is "." or ".." or "*" or ".*" || name.Length == 0) continue;
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;
            long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mtime);
            bool dir = parts[0] == "directory";
            list.Add(new PhoneFile(name, dir, dir ? 0 : size, DateTimeOffset.FromUnixTimeSeconds(mtime)));
        }
        return Sort(list);
    }

    public static IReadOnlyList<PhoneFile> Sort(IEnumerable<PhoneFile> files) =>
        files.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Single-quotes a string for the phone's sh, so names with spaces, quotes or $ stay literal.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string Combine(string folder, string name) =>
        folder.EndsWith('/') ? folder + name : folder + "/" + name;

    /// <summary>The parent folder, or null at the root.</summary>
    public static string? Parent(string path)
    {
        path = Normalize(path);
        if (path == "/") return null;
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    /// <summary>Absolute, forward slashes, no trailing slash, no empty, "." or ".." segments.</summary>
    public static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(seg);
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>A name for a new file or folder that does not collide with existing ones: "New folder (2)".</summary>
    public static string UniqueName(string wanted, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(wanted)) return wanted;
        for (int i = 2; ; i++)
        {
            var candidate = $"{wanted} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Names a phone's filesystem accepts: no slash, not empty, not "." or "..".</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name.Length <= 255 && name != "." && name != ".." && !name.Contains('/') && !name.Contains('\0');

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[u];
    }
}
