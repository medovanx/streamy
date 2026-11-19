namespace Streamy.Services;

/// <summary>A small rolling log in %LOCALAPPDATA%\Streamy\streamy.log for errors that should not interrupt the user.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streamy", "streamy.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(Exception ex, string? context = null) =>
        Write("ERROR", (context is null ? "" : context + ": ") + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
