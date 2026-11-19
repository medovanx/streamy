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
