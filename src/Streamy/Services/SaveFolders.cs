namespace Streamy.Services;

/// <summary>
/// Where screenshots and recordings go. The user's choice from Settings if set, else Pictures\Streamy and
/// Videos\Streamy. When Windows refuses those (Controlled Folder Access blocks unknown apps from writing to
/// Pictures, Videos and Documents), Streamy falls back to a folder under %USERPROFILE%\Streamy, which is never protected.
/// </summary>
public static class SaveFolders
{
    public static string Screenshots => Resolve(AppSettings.Current.ScreenshotFolder, Environment.SpecialFolder.MyPictures, "Screenshots");

    public static string Recordings => Resolve(AppSettings.Current.RecordingFolder, Environment.SpecialFolder.MyVideos, "Recordings");

    public static string Default(Environment.SpecialFolder known) => Path.Combine(Environment.GetFolderPath(known), "Streamy");

    private static string Resolve(string? chosen, Environment.SpecialFolder known, string fallbackName)
    {
        foreach (var dir in new[] { chosen, Default(known) })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (CanWrite(dir)) return dir;
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Streamy", fallbackName);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".streamy-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Cannot write to {dir}: {ex.Message}");
            return false;
        }
    }

    /// <summary>A short form of a folder for messages: "Pictures\Streamy" rather than the full path.</summary>
    public static string Short(string dir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return dir.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? dir[(home.Length + 1)..] : dir;
    }
}
