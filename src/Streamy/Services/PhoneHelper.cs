using System.Reflection;
using Streamy.Core.Apps;

namespace Streamy.Services;

/// <summary>
/// Runs Streamy's phone-side helper: pushes it once per phone per run, lists apps and fetches their icons, which
/// are kept in %LOCALAPPDATA%\Streamy\icons by package and version so a second visit draws them at once.
/// </summary>
public static class PhoneHelper
{
    private static readonly HashSet<string> Pushed = [];
    private static readonly SemaphoreSlim PushGate = new(1, 1);

    public static string IconFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streamy", "icons");

    private static async Task EnsurePushedAsync(string serial, CancellationToken ct)
    {
        await PushGate.WaitAsync(ct);
        try
        {
            if (Pushed.Contains(serial)) return;
            var local = Path.Combine(Path.GetTempPath(), "streamy-helper.jar");
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("streamy-helper.jar")
                ?? throw new FileNotFoundException("This build has no phone helper."))
            using (var dst = File.Create(local))
                await src.CopyToAsync(dst, ct);
            await AdbClient.Default.PushAsync(serial, local, PhoneApps.HelperPath, ct);
            Pushed.Add(serial);
        }
        finally
        {
            PushGate.Release();
        }
    }

    /// <summary>The installed apps; with <paramref name="sizes"/>, their storage too (slower).</summary>
    public static async Task<IReadOnlyList<PhoneApp>> AppsAsync(string serial, bool sizes = false, CancellationToken ct = default)
    {
        await EnsurePushedAsync(serial, ct);
        var r = await AdbClient.Default.RunAsync(["shell", sizes ? PhoneApps.ListWithSizesCommand : PhoneApps.ListCommand], serial, TimeSpan.FromMinutes(2), ct);
        var apps = PhoneApps.Parse(r.Output);
        if (apps.Count == 0) throw new IOException(r.All.Length > 0 ? r.All : "The phone listed no apps.");
        return apps;
    }

    /// <summary>
    /// Copies the app's APK into <paramref name="folder"/> as "Label version.apk", or, for an app split into several
    /// APKs, a folder of that name holding all of them. Returns the path and how many APKs it holds.
    /// </summary>
    public static async Task<(string Path, int Count)> BackupApkAsync(string serial, PhoneApp app, string folder, CancellationToken ct = default)
    {
        var paths = PhoneApps.ParsePaths((await AdbClient.Default.ShellAsync(serial, [PhoneApps.PathCommand(app.Package)], ct)).Output);
        if (paths.Count == 0) throw new IOException("the phone did not say where the app is installed");
        string name = PhoneApps.BackupName(app);
        if (paths.Count > 1)
        {
            var dir = Path.Combine(folder, name);
            Directory.CreateDirectory(dir);
            foreach (var p in paths) await AdbClient.Default.PullAsync(serial, p, dir, ct);
            return (dir, paths.Count);
        }
        var tmp = Path.Combine(folder, $".streamy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await AdbClient.Default.PullAsync(serial, paths[0], tmp, ct);
            var result = Path.Combine(folder, name + ".apk");
            File.Move(Path.Combine(tmp, Path.GetFileName(paths[0])), result, overwrite: true);
            return (result, 1);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    public static string IconFile(PhoneApp app) => Path.Combine(IconFolder, app.IconKey + ".png");

    /// <summary>
    /// Makes sure every app's icon is on disk, asking the phone only for missing ones. If an icon takes the helper
    /// down, that app is skipped and the rest are asked for again. Calls back as icons arrive.
    /// </summary>
    public static async Task FetchIconsAsync(string serial, IReadOnlyList<PhoneApp> apps, Action<PhoneApp> arrived, CancellationToken ct = default)
    {
        Directory.CreateDirectory(IconFolder);
        var missing = apps.Where(a => !File.Exists(IconFile(a))).ToList();
        if (missing.Count == 0) return;
        await EnsurePushedAsync(serial, ct);
        var byPackage = missing.ToDictionary(a => a.Package);
        var queue = new Queue<PhoneApp>(missing);
        while (queue.Count > 0)
        {
            // Batches keep the command line short; each batch is one process on the phone.
            var batch = new List<PhoneApp>();
            while (queue.Count > 0 && batch.Count < 60) batch.Add(queue.Dequeue());
            var r = await AdbClient.Default.RunAsync(["shell", PhoneApps.IconsCommand(batch.Select(a => a.Package))], serial, TimeSpan.FromMinutes(1), ct);
            var done = new HashSet<string>();
            foreach (var (package, png) in PhoneApps.ParseIcons(r.Output))
            {
                done.Add(package);
                if (png is null || !byPackage.TryGetValue(package, out var app)) continue;
                await File.WriteAllBytesAsync(IconFile(app), png, ct);
                arrived(app);
            }
            // The helper died part way: the first unanswered app is the culprit; ask again for the ones after it.
            var unanswered = batch.Where(a => !done.Contains(a.Package)).ToList();
            if (unanswered.Count > 1)
                foreach (var a in unanswered.Skip(1)) queue.Enqueue(a);
            if (unanswered.Count > 0) Log.Info($"icon failed: {unanswered[0].Package}");
        }
    }
}
