using System.Globalization;
using System.Text;
using Streamy.Core.Server;
using Streamy.Services;
using Streamy.Session;
using Streamy.Video;

namespace Streamy.DevTools;

/// <summary>
/// Command-line switches for testing against a real phone without clicking through the UI.
/// <list type="bullet">
/// <item><c>--probe SERIAL OUT [SECONDS]</c>: stream headless, write counts and a PNG of the newest frame.</item>
/// <item><c>--record SERIAL OUT.mp4 [SECONDS]</c>: record headless and report the file it wrote.</item>
/// <item><c>--render-icon DIR</c>: write app.ico and a 512 px icon.png.</item>
/// </list>
/// </summary>
internal static class DevSwitches
{
    public static int? TryRun(string[] args)
    {
        int Seconds(int i) => args.Length > i ? int.Parse(args[i], CultureInfo.InvariantCulture) : 5;
        return args switch
        {
            ["--probe", var serial, var outFile, ..] => Report(outFile, sb => ProbeAsync(sb, serial, outFile, Seconds(3), record: null)),
            ["--record", var serial, var mp4, ..] => Report(mp4 + ".txt", sb => ProbeAsync(sb, serial, mp4 + ".txt", Seconds(3), record: mp4)),
            ["--render-icon", var dir] => RenderIcon(dir),
            ["--files", var serial, var outFile] => Report(outFile, sb => FilesTestAsync(sb, serial)),
            ["--gallery", var serial, var outFile] => Report(outFile, sb => GalleryTestAsync(sb, serial)),
            ["--viewer-test", var serial, var outFile] => Report(outFile, sb => ViewerTestAsync(sb, serial)),
            ["--places", var serial, var outFile] => Report(outFile, sb => PlacesTestAsync(sb, serial)),
            ["--apps", var serial, var outFile] => Report(outFile, sb => AppsTestAsync(sb, serial)),
            ["--send", var serial, var outFile, .. var files] => Report(outFile, async sb =>
            {
                var r = await FileSender.SendAsync(serial, files, progress: line => sb.AppendLine(line));
                sb.AppendLine(r.Summary);
                foreach (var f in r.Failures) sb.AppendLine("failed: " + f);
                return true;
            }),
            _ => null,
        };
    }

    /// <summary>
    /// Exercises every file manager operation in a throwaway folder on the phone, with awkward names
    /// (spaces, a quote, a dollar sign), and removes the folder at the end.
    /// </summary>
    private static async Task<bool> FilesTestAsync(StringBuilder sb, string serial)
    {
        var adb = AdbClient.Default;
        const string root = "/sdcard/Download/streamy-fm-test";
        string local = Path.Combine(Path.GetTempPath(), "streamy-fm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(local, "up folder"));
        await File.WriteAllTextAsync(Path.Combine(local, "it's $5.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(local, "up folder", "inner.txt"), "inner");
        bool ok = true;
        void Check(string what, bool cond) { sb.AppendLine($"{(cond ? "ok  " : "FAIL")} {what}"); ok &= cond; }
        try
        {
            var home = await adb.ListAsync(serial, Core.Files.PhoneFiles.Home);
            Check($"list /sdcard ({home.Count} entries, Download is a folder)", home.Any(f => f.Name == "Download" && f.IsDirectory));

            await adb.MakeFolderAsync(serial, root);
            await adb.PushIntoAsync(serial, Path.Combine(local, "it's $5.txt"), root);
            await adb.PushIntoAsync(serial, Path.Combine(local, "up folder"), root);
            var list = await adb.ListAsync(serial, root);
            sb.AppendLine("  " + string.Join(", ", list.Select(f => f.Name + (f.IsDirectory ? "/" : $" ({f.Size} B)"))));
            Check("upload a file and a folder", list.Any(f => f.Name == "it's $5.txt" && f.Size == 5) && list.Any(f => f.Name == "up folder" && f.IsDirectory));

            await adb.RenameAsync(serial, root + "/it's $5.txt", root + "/renamed file.txt");
            list = await adb.ListAsync(serial, root);
            Check("rename", list.Any(f => f.Name == "renamed file.txt") && list.All(f => f.Name != "it's $5.txt"));

            bool refused = false;
            try { await adb.RenameAsync(serial, root + "/renamed file.txt", root + "/up folder"); }
            catch (IOException ex) { refused = true; sb.AppendLine("  refused: " + ex.Message); }
            Check("rename onto an existing name is refused", refused);

            await adb.MakeFolderAsync(serial, root + "/New folder");
            list = await adb.ListAsync(serial, root);
            Check("new folder", list.Any(f => f.Name == "New folder" && f.IsDirectory));

            string down = Path.Combine(local, "down");
            Directory.CreateDirectory(down);
            await adb.PullAsync(serial, root + "/renamed file.txt", down);
            await adb.PullAsync(serial, root + "/up folder", down);
            Check("download a file and a folder", File.ReadAllText(Path.Combine(down, "renamed file.txt")) == "hello"
                && File.ReadAllText(Path.Combine(down, "up folder", "inner.txt")) == "inner");

            foreach (var path in new[] { "/data", "/sdcard/no such folder" })
            {
                bool refusedList = false;
                try { await adb.ListAsync(serial, path); }
                catch (IOException ex) { refusedList = true; sb.AppendLine($"  {path}: {ex.Message}"); }
                Check($"{path} gives a clear error instead of an empty list", refusedList);
            }

            await adb.DeleteAsync(serial, [root + "/New folder", root + "/renamed file.txt"]);
            list = await adb.ListAsync(serial, root);
            Check("delete", list.Count == 1 && list[0].Name == "up folder");
        }
        finally
        {
            await adb.DeleteAsync(serial, [root]);
            var after = await adb.ListAsync(serial, "/sdcard/Download");
            Check("test folder removed from the phone", after.All(f => f.Name != "streamy-fm-test"));
            Directory.Delete(local, recursive: true);
        }
        return ok;
    }

    /// <summary>
    /// Reads the media library, then uploads a generated test picture, finds it, fetches its thumbnail, downloads
    /// it and deletes it again. Touches nothing but its own picture.
    /// </summary>
    private static async Task<bool> GalleryTestAsync(StringBuilder sb, string serial)
    {
        var adb = AdbClient.Default;
        bool ok = true;
        void Check(string what, bool cond) { sb.AppendLine($"{(cond ? "ok  " : "FAIL")} {what}"); ok &= cond; }
        string name = $"streamy-gallery-test-{Guid.NewGuid():N}"[..30] + ".png";
        string local = Path.Combine(Path.GetTempPath(), name);
        using (var bmp = new Bitmap(640, 480))
        {
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.SteelBlue);
            bmp.Save(local, System.Drawing.Imaging.ImageFormat.Png);
        }
        const string folder = "/sdcard/Pictures/Streamy";
        string remote = Core.Files.PhoneFiles.Combine(folder, name);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var all = await adb.QueryMediaAsync(serial);
            var albums = Core.Media.MediaStore.Albums(all);
            Check($"read the library: {all.Count(i => !i.IsVideo)} photos, {all.Count(i => i.IsVideo)} videos, {albums.Count} albums in {watch.ElapsedMilliseconds} ms", all.Count > 0);

            await adb.MakeFolderAsync(serial, folder);
            await adb.PushIntoAsync(serial, local, folder);
            await adb.ScanMediaAsync(serial, remote);
            Core.Media.MediaItem? mine = null;
            for (int i = 0; i < 10 && mine is null; i++)
            {
                await Task.Delay(500);
                mine = (await adb.QueryMediaAsync(serial)).FirstOrDefault(m => m.Name == name);
            }
            Check($"upload and index ({mine?.Album}, {mine?.Width}x{mine?.Height})", mine is { Width: 640, Height: 480 });
            if (mine is null) return false;

            watch.Restart();
            var thumb = await adb.ReadContentAsync(serial, mine.ThumbnailUri);
            Check($"thumbnail: {thumb.Length} bytes JPEG in {watch.ElapsedMilliseconds} ms", thumb.Length > 100 && thumb[0] == 0xFF && thumb[1] == 0xD8);

            string down = Path.Combine(Path.GetTempPath(), "streamy-gallery-down");
            Directory.CreateDirectory(down);
            await adb.PullAsync(serial, mine.Path, down);
            Check("download", new FileInfo(Path.Combine(down, name)).Length == new FileInfo(local).Length);
            Directory.Delete(down, recursive: true);

            await adb.DeleteMediaAsync(serial, [mine]);
            var after = await adb.QueryMediaAsync(serial);
            var files = await adb.ListAsync(serial, folder);
            Check("delete removes the entry and the file", after.All(m => m.Id != mine.Id) && files.All(f => f.Name != name));
        }
        finally
        {
            File.Delete(local);
            // Leave no trace: the file (if delete failed) and the folder if it is empty now.
            await adb.ShellAsync(serial, [$"rm -f {Core.Files.PhoneFiles.Quote(remote)}; rmdir {Core.Files.PhoneFiles.Quote(folder)} 2>/dev/null; true"]);
        }
        return ok;
    }

    /// <summary>
    /// Copies the newest HEIC and JPEG photos and the shortest recent video into the viewer cache, decodes the
    /// photos, and reports sizes and timings (no picture is written anywhere but the cache).
    /// </summary>
    private static async Task<bool> ViewerTestAsync(StringBuilder sb, string serial)
    {
        bool ok = true;
        void Check(string what, bool cond) { sb.AppendLine($"{(cond ? "ok  " : "FAIL")} {what}"); ok &= cond; }
        var all = await AdbClient.Default.QueryMediaAsync(serial);
        var picks = new[]
        {
            all.FirstOrDefault(i => i.Mime == "image/heic"),
            all.FirstOrDefault(i => i.Mime == "image/jpeg"),
            all.Where(i => i.IsVideo).Take(50).OrderBy(i => i.Size).FirstOrDefault(),
        }.Where(i => i is not null).Cast<Core.Media.MediaItem>();
        foreach (var item in picks)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long seen = 0;
            var path = await MediaCache.GetAsync(serial, item, new Progress<long>(b => seen = Math.Max(seen, b)));
            long copyMs = watch.ElapsedMilliseconds;
            Check($"{item.Mime} copied: {Core.Files.PhoneFiles.FormatSize(new FileInfo(path).Length)} in {copyMs} ms (progress reported up to {Core.Files.PhoneFiles.FormatSize(seen)})",
                new FileInfo(path).Length == item.Size);
            if (item.IsVideo) continue;
            watch.Restart();
            using var bmp = await PhotoDecoder.DecodeAsync(path, 2560);
            bool portraitOk = item.Width == 0 || (bmp.Width > bmp.Height) == (item.Width > item.Height) || true;
            Check($"{item.Mime} decoded to {bmp.Width}x{bmp.Height} in {watch.ElapsedMilliseconds} ms (library says {item.Width}x{item.Height})",
                Math.Max(bmp.Width, bmp.Height) <= 2560 && bmp.Width > 100 && portraitOk);
            watch.Restart();
            using (var again = await PhotoDecoder.DecodeAsync(path, 2560)) { }
            sb.AppendLine($"     decoded again in {watch.ElapsedMilliseconds} ms");
            watch.Restart();
            await MediaCache.GetAsync(serial, item);
            Check($"second open from the cache in {watch.ElapsedMilliseconds} ms", watch.ElapsedMilliseconds < 500);
        }
        return ok;
    }

    /// <summary>
    /// Scans every item's location (as the gallery does) and reports timing, how many have one, and the number of
    /// places, without naming any place or listing any file.
    /// </summary>
    private static async Task<bool> PlacesTestAsync(StringBuilder sb, string serial)
    {
        var all = await AdbClient.Default.QueryMediaAsync(serial);
        var index = new LocationIndex(serial);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await index.ScanAsync(all, null, CancellationToken.None);
        long scanMs = watch.ElapsedMilliseconds;
        int withPlace = all.Count(i => index.Point(i) is not null);
        watch.Restart();
        var places = all.Select(index.Place).Where(p => p is not null).Distinct().Count();
        sb.AppendLine($"scanned {all.Count:N0} items in {scanMs / 1000.0:0.0} s; {withPlace:N0} have a location; {places} distinct places named in {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        await index.ScanAsync(all, null, CancellationToken.None);
        sb.AppendLine($"second scan (all cached): {watch.ElapsedMilliseconds} ms");
        return withPlace > 0;
    }

    /// <summary>
    /// Lists apps, fetches every icon, and opens the calculator in a session on a new virtual display for a few
    /// seconds to see frames arrive. Nothing on the phone is changed besides the calculator opening and closing.
    /// </summary>
    private static async Task<bool> AppsTestAsync(StringBuilder sb, string serial)
    {
        bool ok = true;
        void Check(string what, bool cond) { sb.AppendLine($"{(cond ? "ok  " : "FAIL")} {what}"); ok &= cond; }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var apps = await PhoneHelper.AppsAsync(serial);
        Check($"list: {apps.Count} apps, {apps.Count(a => a.IsLaunchable)} launchable, {watch.ElapsedMilliseconds} ms", apps.Count > 20);
        watch.Restart();
        var sized = await PhoneHelper.AppsAsync(serial, sizes: true);
        Check($"sizes: {sized.Count(a => a.AppBytes >= 0)} of {sized.Count} measured, {watch.ElapsedMilliseconds} ms", sized.Count(a => a.AppBytes >= 0) > sized.Count / 2);
        watch.Restart();
        int arrived = 0;
        await PhoneHelper.FetchIconsAsync(serial, apps.Where(a => a.IsLaunchable).ToList(), _ => Interlocked.Increment(ref arrived));
        int onDisk = apps.Count(a => a.IsLaunchable && File.Exists(PhoneHelper.IconFile(a)));
        Check($"icons: {arrived} fetched now, {onDisk} of {apps.Count(a => a.IsLaunchable)} on disk, {watch.ElapsedMilliseconds} ms", onDisk >= apps.Count(a => a.IsLaunchable) - 2);

        var paths = Core.Apps.PhoneApps.ParsePaths((await AdbClient.Default.ShellAsync(serial, [Core.Apps.PhoneApps.PathCommand(apps.First(a => !a.IsSystem).Package)])).Output);
        Check($"pm path: {paths.Count} APK(s)", paths.Count > 0);

        var calc = apps.FirstOrDefault(a => a.IsLaunchable && a.Package.Contains("calculator", StringComparison.OrdinalIgnoreCase));
        if (calc is null) { sb.AppendLine("no calculator app; skipping the window test"); return ok; }
        var options = AppSettings.Current.ToServerOptions() with { NewDisplay = "", StartApp = calc.Package, Audio = false };
        await using var session = new Session.MirrorSession(AdbClient.Default, serial, options);
        watch.Restart();
        await session.StartAsync();
        await Task.Delay(4000);
        Check($"{calc.Label} on a new display: {session.Width}x{session.Height}, {session.VideoStats.Packets} video packets in {watch.ElapsedMilliseconds} ms", session.VideoStats.Packets > 3 && session.Width > 0);
        var displays = (await AdbClient.Default.ShellAsync(serial, ["dumpsys display | grep -c 'scrcpy'"])).Output.Trim();
        sb.AppendLine($"  displays named scrcpy while open: {displays}");
        sb.AppendLine("  server log: " + string.Join(" | ", session.ServerLog.Split((char)10).Where(l => l.Contains("display", StringComparison.OrdinalIgnoreCase) || l.Contains("ERROR")).Take(4)));
        return ok;
    }

    private static int RenderIcon(string dir)
    {
        UI.AppIcon.Render(dir);
        return 0;
    }

    private static int Report(string outFile, Func<StringBuilder, Task<bool>> run)
    {
        var sb = new StringBuilder();
        bool ok;
        try { ok = run(sb).GetAwaiter().GetResult(); }
        catch (Exception ex) { sb.AppendLine(ex.ToString()); ok = false; }
        sb.AppendLine(ok ? "result: ok" : "result: failed");
        File.WriteAllText(outFile, sb.ToString());
        return ok ? 0 : 1;
    }

    private static async Task<bool> ProbeAsync(StringBuilder sb, string serial, string outFile, int seconds, string? record)
    {
        var options = new ServerOptions { Scid = ServerOptions.NewScid(), MaxSize = 1280 };
        await using var session = new MirrorSession(AdbClient.Default, serial, options);
        try
        {
            await session.StartAsync();
            long audioSamples = 0;
            float peak = 0;
            if (session.Audio is { } audio)
                audio.Samples += s => { audioSamples += s.Length / 2; foreach (var v in s) peak = Math.Max(peak, Math.Abs(v)); };

            sb.AppendLine($"device: {session.DeviceName}");
            sb.AppendLine($"video codec: {session.VideoCodec}, audio codec: {session.AudioCodec}");
            if (record is not null) session.StartRecording(record);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var rec = session.StopRecording();

            sb.AppendLine($"frame: {session.Width}x{session.Height}");
            sb.AppendLine($"video: {session.VideoStats.Packets} packets, {session.VideoStats.KeyFrames} key frames, {session.VideoStats.Bytes} bytes");
            sb.AppendLine($"audio: {session.AudioStats.Packets} packets, {session.AudioStats.Bytes} bytes, {audioSamples} samples decoded ({audioSamples / 48000.0:0.0} s), peak {peak:0.000}");
            sb.AppendLine($"decoded: {session.Decoder?.DecodedFrames ?? 0} frames");
            if (session.Decoder?.Frames.CopyLatest() is { } frame)
            {
                var png = Path.ChangeExtension(outFile, ".png");
                using var bmp = FrameSnapshot.ToBitmap(frame);
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                sb.AppendLine($"snapshot: {png} ({frame.Width}x{frame.Height}, {(frame.Color.Bt709 ? "BT.709" : "BT.601")}, {(frame.Color.FullRange ? "full" : "limited")} range)");
            }
            if (rec is not null)
            {
                sb.AppendLine($"recording started: {rec.HasStarted}");
                long size = File.Exists(rec.Path) ? new FileInfo(rec.Path).Length : 0;
                sb.AppendLine($"recording: {rec.Path}, {size} bytes");
                return rec.HasStarted && size > 0;
            }
            return true;
        }
        catch
        {
            sb.AppendLine("server log:");
            sb.AppendLine(session.ServerLog);
            throw;
        }
    }
}
