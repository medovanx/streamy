using System.Globalization;
using Streamy.Core.Protocol;

namespace Streamy.Core.Server;

/// <summary>
/// Options passed to scrcpy-server as key=value arguments. Only non-default values are written,
/// matching what the scrcpy client does (app/src/server.c), so the server's own defaults apply otherwise.
/// </summary>
public sealed record ServerOptions
{
    /// <summary>31-bit session id; names the device socket so several sessions can run on one phone.</summary>
    public required uint Scid { get; init; }

    public string LogLevel { get; init; } = "info";
    public bool Video { get; init; } = true;
    public bool Audio { get; init; } = true;
    public bool Control { get; init; } = true;

    /// <summary>"h264", "h265" or "av1". Null keeps the server default (h264).</summary>
    public string? VideoCodec { get; init; }

    /// <summary>"opus", "aac", "flac" or "raw". Null keeps the server default (opus).</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Bits per second; 0 keeps the server default (8 Mbps).</summary>
    public int VideoBitRate { get; init; }

    /// <summary>Largest side of the video in pixels; 0 means the device's own size.</summary>
    public int MaxSize { get; init; }

    /// <summary>0 means unlimited.</summary>
    public int MaxFps { get; init; }

    /// <summary>The device listens and the PC connects (adb forward). Streamy always uses this: it works the same over USB and Wi-Fi and needs no free port on the PC side in advance.</summary>
    public bool TunnelForward { get; init; } = true;

    public bool StayAwake { get; init; }
    public bool ShowTouches { get; init; }
    public bool PowerOffOnClose { get; init; }
    public bool ClipboardAutosync { get; init; } = true;

    /// <summary>"output" (device sound, default), "playback" (app audio, Android 13+) or "mic".</summary>
    public string? AudioSource { get; init; }

    /// <summary>Keep sound playing on the phone too (only with audio source "playback").</summary>
    public bool AudioDup { get; init; }

    public static uint NewScid() => (uint)Random.Shared.Next() & 0x7FFFFFFF;

    public string SocketName => ScrcpyProtocol.SocketName(Scid);

    public IReadOnlyList<string> ToArguments()
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "scid=" + Scid.ToString("x8", inv),
            "log_level=" + LogLevel,
        };
        if (!Video) args.Add("video=false");
        if (Video && VideoBitRate > 0) args.Add("video_bit_rate=" + VideoBitRate.ToString(inv));
        if (!Audio) args.Add("audio=false");
        if (Video && VideoCodec is { } vc && vc != "h264") args.Add("video_codec=" + vc);
        if (Audio && AudioCodec is { } ac && ac != "opus") args.Add("audio_codec=" + ac);
        if (Audio && AudioSource is { } src && src != "output") args.Add("audio_source=" + src);
        if (Audio && AudioDup) args.Add("audio_dup=true");
        if (MaxSize > 0) args.Add("max_size=" + MaxSize.ToString(inv));
        if (MaxFps > 0) args.Add("max_fps=" + MaxFps.ToString(inv));
        if (TunnelForward) args.Add("tunnel_forward=true");
        if (!Control) args.Add("control=false");
        if (ShowTouches) args.Add("show_touches=true");
        if (StayAwake) args.Add("stay_awake=true");
        if (PowerOffOnClose) args.Add("power_off_on_close=true");
        if (!ClipboardAutosync) args.Add("clipboard_autosync=false");
        return args;
    }

    /// <summary>The arguments after "adb -s SERIAL shell" that start the server.</summary>
    public IReadOnlyList<string> ToShellCommand() =>
    [
        "CLASSPATH=" + ScrcpyProtocol.DeviceServerPath,
        "app_process",
        "/",
        ScrcpyProtocol.ServerMainClass,
        ScrcpyProtocol.ServerVersion,
        .. ToArguments(),
    ];
}
