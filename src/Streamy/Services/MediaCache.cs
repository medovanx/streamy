using Streamy.Core.Media;

namespace Streamy.Services;

/// <summary>
/// Full-size copies of photos and videos for the built-in viewer, kept in %LOCALAPPDATA%\Streamy\media\&lt;phone&gt;
/// so swiping back to an item is instant. The oldest files are removed once the folder passes 2 GB.
/// </summary>
public static class MediaCache
{
    private const long Limit = 2L * 1024 * 1024 * 1024;
    private static readonly SemaphoreSlim Trim = new(1, 1);

    private static string Folder(string serial)
    {
        string safe = string.Concat(serial.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streamy", "media", safe);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The local file if it was already copied, else null.</summary>
    public static string? Cached(string serial, MediaItem item)
    {
        var file = PathFor(serial, item);
        return File.Exists(file) ? file : null;
    }

    private static string PathFor(string serial, MediaItem item) =>
        Path.Combine(Folder(serial), Path.GetFileNameWithoutExtension(item.CacheKey) + Path.GetExtension(item.Name).ToLowerInvariant());

    /// <summary>
    /// Copies the item from the phone (once) and returns the local path. Reports bytes copied so far, read from the
    /// growing file, about five times a second.
    /// </summary>
    public static async Task<string> GetAsync(string serial, MediaItem item, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var target = PathFor(serial, item);
        if (File.Exists(target))
        {
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // most recently used
            return target;
        }
        var temp = Path.Combine(Folder(serial), "incoming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var pull = AdbClient.Default.PullAsync(serial, item.Path, temp, ct);
            var partial = Path.Combine(temp, item.Name);
            while (!pull.IsCompleted)
            {
                await Task.WhenAny(pull, Task.Delay(200, ct));
                if (progress is not null && File.Exists(partial))
                {
                    try { progress.Report(new FileInfo(partial).Length); } catch (IOException) { }
                }
            }
            await pull;
            File.Move(partial, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        _ = TrimAsync(serial);
        return target;
    }

    private static async Task TrimAsync(string serial)
    {
        if (!await Trim.WaitAsync(0)) return;
        try
        {
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(Folder(serial)).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                foreach (var f in files)
                {
                    if (total <= Limit) break;
                    try { total -= f.Length; f.Delete(); } catch (IOException) { }
                }
            });
        }
        finally
        {
            Trim.Release();
        }
    }
}
