using System.Text.Json;
using Streamy.Core.Server;

namespace Streamy.Services;

/// <summary>A Wi-Fi phone Streamy reconnects to by itself.</summary>
public sealed record SavedDevice(string Endpoint, string Name);

/// <summary>User settings, stored in %APPDATA%\Streamy\settings.json. A missing or damaged file gives the defaults.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Streamy", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---- Video ----

    /// <summary>Largest side of the picture in pixels; 0 keeps the phone's own resolution.</summary>
    public int MaxSize { get; set; }

    /// <summary>Video bit rate in Mbit/s.</summary>
    public int BitRateMbps { get; set; } = 8;

    /// <summary>Frame rate cap; 0 means as fast as the phone draws.</summary>
    public int MaxFps { get; set; }

    /// <summary>"h264", "h265" or "av1".</summary>
    public string VideoCodec { get; set; } = "h264";

    // ---- Audio ----

    /// <summary>Stream the phone's sound (Android 11+).</summary>
    public bool Audio { get; set; } = true;

    /// <summary>Mute the phone's sound on the PC.</summary>
    public bool Muted { get; set; }

    /// <summary>Playback volume on the PC, 0 to 100.</summary>
    public int Volume { get; set; } = 100;

    // ---- Phone ----

    /// <summary>Show a dot on the phone's screen wherever it is touched, while a session runs.</summary>
    public bool ShowTouches { get; set; }

    /// <summary>Keep the phone awake while it is plugged in and mirrored.</summary>
    public bool StayAwake { get; set; }

    /// <summary>Turn the phone's own screen off when mirroring starts (the mirror keeps showing it).</summary>
    public bool TurnScreenOff { get; set; }

    /// <summary>Lock the phone when its window is closed.</summary>
    public bool PowerOffOnClose { get; set; }

    // ---- Window ----

    public bool AlwaysOnTop { get; set; }

    /// <summary>Folder for screenshots; null means Pictures\Streamy.</summary>
    public string? ScreenshotFolder { get; set; }

    /// <summary>Folder for recordings; null means Videos\Streamy.</summary>
    public string? RecordingFolder { get; set; }

    // ---- App ----

    public bool CheckForUpdates { get; set; } = true;

    public List<SavedDevice> SavedDevices { get; set; } = [];

    public static AppSettings Current { get; } = Load();

    /// <summary>The server options for a new session from these settings.</summary>
    public ServerOptions ToServerOptions() => new()
    {
        Scid = ServerOptions.NewScid(),
        MaxSize = MaxSize,
        VideoBitRate = Math.Clamp(BitRateMbps, 1, 100) * 1_000_000,
        MaxFps = MaxFps,
        VideoCodec = VideoCodec,
        Audio = Audio,
        ShowTouches = ShowTouches,
        StayAwake = StayAwake,
        PowerOffOnClose = PowerOffOnClose,
    };

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to save a preference should never break mirroring.
        }
    }
}
