using System.Text.Json;

namespace Streamy.Services;

/// <summary>User settings, stored in %APPDATA%\Streamy\settings.json. A missing or damaged file gives the defaults.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Streamy", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Show a dot on the phone's screen wherever it is touched, while a session runs.</summary>
    public bool ShowTouches { get; set; }

    /// <summary>Mute the phone's sound on the PC.</summary>
    public bool Muted { get; set; }

    /// <summary>Playback volume on the PC, 0 to 100.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Folder for screenshots; null means Pictures\Streamy.</summary>
    public string? ScreenshotFolder { get; set; }

    /// <summary>Folder for recordings; null means Videos\Streamy.</summary>
    public string? RecordingFolder { get; set; }

    public static AppSettings Current { get; } = Load();

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
