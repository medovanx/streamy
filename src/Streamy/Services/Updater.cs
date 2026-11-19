using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Streamy.Services;

/// <summary>
/// Checks GitHub Releases for a newer tagged version (vMAJOR.MINOR.PATCH) and installs it in place, the same way
/// Data Tide does: download next to the running exe, verify the SHA-256 GitHub publishes for the asset, rename the
/// running exe to .old (Windows allows that), move the new one in, restart.
/// </summary>
public static class Updater
{
    public const string Repo = "medovanx/streamy";
    public const string AssetName = "Streamy.exe";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public sealed record Release(Version Version, string Tag, string PageUrl, string AssetUrl, string? Sha256);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Streamy/{CurrentText}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current => Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => Current.ToString(3);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>The newer release, or null when this is the latest. Throws when GitHub cannot be reached.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var r = doc.RootElement;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = Normalize(v);
        if (v <= Current) return null;

        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:", StringComparison.Ordinal)
                ? ds["sha256:".Length..] : null;
            return new Release(v, tag, r.GetProperty("html_url").GetString() ?? ReleasesPage,
                               a.GetProperty("browser_download_url").GetString()!, sha);
        }
        return null;
    }

    public enum Phase { Downloading, Verifying, Installing }

    /// <summary>Where an update is: the phase, and bytes downloaded of the total (0 when GitHub does not say).</summary>
    public readonly record struct Progress(Phase Phase, long Done, long Total);

    /// <summary>
    /// Downloads the release's exe to <paramref name="target"/> and checks its SHA-256. Reports progress at most
    /// ten times a second. Throws on failure or cancellation and leaves no partial file behind.
    /// </summary>
    public static async Task DownloadAsync(Release rel, string target, IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using (var resp = await Http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
                await using var fs = File.Create(target);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[81920];
                var last = DateTime.MinValue;
                int n;
                progress?.Report(new Progress(Phase.Downloading, 0, total));
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds >= 100)
                    {
                        last = DateTime.UtcNow;
                        progress?.Report(new Progress(Phase.Downloading, done, total));
                    }
                }
                progress?.Report(new Progress(Phase.Downloading, done, total));
            }

            if (rel.Sha256 is not null)
            {
                progress?.Report(new Progress(Phase.Verifying, 0, 0));
                await using var fs = File.OpenRead(target);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (actual != rel.Sha256.ToLowerInvariant())
                    throw new InvalidDataException("The download did not match the checksum GitHub published.");
            }
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Downloads and verifies the new exe, swaps it in for the running one and starts it. The caller then exits.
    /// Throws on failure or cancellation; the running exe is untouched in that case.
    /// </summary>
    public static async Task InstallAsync(Release rel, IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath!;
        string fresh = exe + ".new", old = exe + ".old";
        try
        {
            await DownloadAsync(rel, fresh, progress, ct);
            ct.ThrowIfCancellationRequested();
            progress?.Report(new Progress(Phase.Installing, 0, 0));

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "update");
            try { if (File.Exists(fresh)) File.Delete(fresh); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Removes the previous exe left behind by an update.</summary>
    public static void CleanupOld()
    {
        try
        {
            var old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still locked: next start */ }
    }

    public static void OpenPage(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
