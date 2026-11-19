using System.Globalization;
using System.Text;
using Streamy.Core.Server;
using Streamy.Services;
using Streamy.Session;
using Streamy.UI;

namespace Streamy;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--probe", var serial, var outFile, ..])
        {
            int seconds = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
            return ProbeAsync(serial, outFile, seconds).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        var main = new MainWindow();
        if (args is ["--mirror", var mirrorSerial, ..]) main.Shown += async (_, _) => await main.MirrorAsync(mirrorSerial);
        Application.Run(main);
        return 0;
    }

    /// <summary>
    /// Developer switch: start one session without a window, stream for a few seconds and write what came back.
    /// Proves the handshake and packet parsing against a real phone: streamy --probe SERIAL OUT [SECONDS]
    /// </summary>
    private static async Task<int> ProbeAsync(string serial, string outFile, int seconds)
    {
        var sb = new StringBuilder();
        var options = new ServerOptions { Scid = ServerOptions.NewScid(), MaxSize = 1280 };
        await using var session = new MirrorSession(AdbClient.Default, serial, options);
        try
        {
            await session.StartAsync();
            sb.AppendLine($"device: {session.DeviceName}");
            sb.AppendLine($"video codec: {session.VideoCodec}, audio codec: {session.AudioCodec}");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            sb.AppendLine($"frame: {session.Width}x{session.Height}");
            sb.AppendLine($"video: {session.VideoStats.Packets} packets, {session.VideoStats.KeyFrames} key frames, {session.VideoStats.Bytes} bytes");
            sb.AppendLine($"audio: {session.AudioStats.Packets} packets, {session.AudioStats.Bytes} bytes");
            sb.AppendLine($"decoded: {session.Decoder?.DecodedFrames ?? 0} frames");
            if (session.Decoder?.Frames.Take() is { } frame)
            {
                var png = Path.ChangeExtension(outFile, ".png");
                using var bmp = Video.FrameSnapshot.ToBitmap(frame);
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                sb.AppendLine($"snapshot: {png} ({frame.Width}x{frame.Height}, {(frame.Color.Bt709 ? "BT.709" : "BT.601")}, {(frame.Color.FullRange ? "full" : "limited")} range)");
            }
            sb.AppendLine("result: ok");
            return Finish(0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("result: failed");
            sb.AppendLine(ex.ToString());
            sb.AppendLine("server log:");
            sb.AppendLine(session.ServerLog);
            return Finish(1);
        }

        int Finish(int code)
        {
            File.WriteAllText(outFile, sb.ToString());
            return code;
        }
    }
}
