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
            _ => null,
        };
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
