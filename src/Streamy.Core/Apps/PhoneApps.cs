using System.Globalization;
using Streamy.Core.Files;

namespace Streamy.Core.Apps;

/// <summary>An app installed on the phone, as Streamy's helper reports it.</summary>
public sealed record PhoneApp(
    string Package,
    string Label,
    string VersionName,
    long VersionCode,
    bool IsSystem,
    bool IsDisabled,
    bool IsLaunchable,
    DateTimeOffset Installed,
    DateTimeOffset Updated,
    long ApkBytes,
    long AppBytes,
    long DataBytes,
    long CacheBytes)
{
    /// <summary>App, data and cache together, as Android's own "Storage" screen adds them up; the APK size when unknown.</summary>
    public long TotalBytes => AppBytes >= 0 ? AppBytes + Math.Max(0, DataBytes) + Math.Max(0, CacheBytes) : ApkBytes;

    /// <summary>Names the cached icon: a new version may bring a new icon.</summary>
    public string IconKey => $"{Package}_{VersionCode.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Streamy's phone-side helper (helper/src, built to streamy-helper.jar) and the shell commands for apps. The helper
/// runs with app_process as the shell user, like scrcpy-server: it lists apps with their labels, versions and sizes
/// and draws their icons, which adb alone cannot.
/// </summary>
public static class PhoneApps
{
    public const string HelperPath = "/data/local/tmp/streamy-helper.jar";
    public const int IconSize = 96;

    private static string Helper(string args) => $"CLASSPATH={HelperPath} app_process / dev.streamy.helper.Main {args}";

    /// <summary>Quick: no sizes (they read -1). <see cref="ListWithSizesCommand"/> adds them, taking seconds longer.</summary>
    public static string ListCommand => Helper("apps");

    public static string ListWithSizesCommand => Helper("apps sizes");

    public static string IconsCommand(IEnumerable<string> packages) =>
        Helper($"icons {IconSize.ToString(CultureInfo.InvariantCulture)} " + string.Join(' ', packages.Select(PhoneFiles.Quote)));

    /// <summary>Parses the helper's "apps" output: one tab-separated line per app.</summary>
    public static IReadOnlyList<PhoneApp> Parse(string output)
    {
        var apps = new List<PhoneApp>();
        foreach (var raw in output.Split('\n'))
        {
            var f = raw.TrimEnd('\r').Split('\t');
            if (f.Length < 8 || f[0].Length == 0 || f[0].Contains(' ')) continue;
            long N(int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1;
            string flags = f[4];
            apps.Add(new PhoneApp(
                f[0],
                f[1].Length > 0 ? f[1] : f[0],
                f[2],
                N(3),
                flags.Contains('s'),
                flags.Contains('d'),
                flags.Contains('l'),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, N(5))),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, N(6))),
                N(7),
                N(8),
                N(9),
                N(10)));
        }
        return apps.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Parses "package TAB base64 PNG" lines; a "-" means the phone could not draw that icon.</summary>
    public static IEnumerable<(string Package, byte[]? Png)> ParseIcons(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var data = line[(tab + 1)..];
            byte[]? png = null;
            if (data != "-")
            {
                try { png = Convert.FromBase64String(data); }
                catch (FormatException) { continue; } // cut short: the process died mid-line
            }
            yield return (line[..tab], png);
        }
    }

    /// <summary>Opens the app's launcher activity, like tapping its icon.</summary>
    public static string LaunchCommand(string package) =>
        $"monkey -p {PhoneFiles.Quote(package)} -c android.intent.category.LAUNCHER 1";

    public static string ForceStopCommand(string package) => "am force-stop " + PhoneFiles.Quote(package);

    public static string ClearDataCommand(string package) => "pm clear " + PhoneFiles.Quote(package);

    public static string UninstallCommand(string package) => "pm uninstall " + PhoneFiles.Quote(package);

    /// <summary>"pm path" lists the base APK and any split APKs.</summary>
    public static string PathCommand(string package) => "pm path " + PhoneFiles.Quote(package);

    public static IReadOnlyList<string> ParsePaths(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l["package:".Length..]).ToList();

    /// <summary>A file name for a backed-up APK: "WhatsApp 2.26.44.90.apk".</summary>
    public static string BackupName(PhoneApp app)
    {
        var name = $"{app.Label} {app.VersionName}".Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? app.Package : name;
    }
}
