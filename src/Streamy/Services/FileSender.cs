using Streamy.Core.Adb;

namespace Streamy.Services;

/// <summary>
/// Sends files dropped on a phone window: APKs are installed (or updated, keeping their data), anything else is
/// copied to the phone's Download folder and announced to the media scanner so it shows up in Gallery and Files.
/// </summary>
public static class FileSender
{
    public const string PushFolder = "/sdcard/Download/";

    public sealed record Result(int Installed, int Copied, IReadOnlyList<string> Failures)
    {
        public string Summary => Failures.Count == 0
            ? string.Join(", ", new[]
              {
                  Installed > 0 ? (Installed == 1 ? "Installed 1 app" : $"Installed {Installed} apps") : null,
                  Copied > 0 ? (Copied == 1 ? "Sent 1 file to Download" : $"Sent {Copied} files to Download") : null,
              }.Where(s => s is not null))
            : $"{Installed + Copied} done, {Failures.Count} failed";
    }

    /// <param name="progress">Called before each file with a short status line.</param>
    /// <param name="scan">Asks the phone to index a pushed file (the session's ScanFile message), if a session is open.</param>
    public static async Task<Result> SendAsync(string serial, IReadOnlyList<string> files, Action<string>? progress = null, Action<string>? scan = null)
    {
        int installed = 0, copied = 0;
        var failures = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var name = Path.GetFileName(file);
            string counter = files.Count > 1 ? $" ({i + 1}/{files.Count})" : "";
            try
            {
                if (file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Invoke($"Installing {name}{counter}");
                    var r = await AdbClient.Default.InstallAsync(serial, file);
                    if (AdbParsers.IsInstallSuccess(r.All)) installed++;
                    else failures.Add($"{name}: {AdbParsers.InstallFailure(r.All) ?? r.All.Trim()}");
                }
                else
                {
                    progress?.Invoke($"Sending {name}{counter}");
                    await AdbClient.Default.PushAsync(serial, file, PushFolder + name);
                    scan?.Invoke(PushFolder + name);
                    copied++;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "send " + name);
                failures.Add($"{name}: {ex.Message}");
            }
        }
        return new Result(installed, copied, failures);
    }
}
