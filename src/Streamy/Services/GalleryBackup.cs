using Streamy.Core.Media;

namespace Streamy.Services;

/// <summary>
/// Copies a phone's photos and videos into a PC folder, keeping the phone's folders, and only what is not there
/// yet. Files of one folder are pulled together (one adb call for up to 40 files), which is far quicker than one
/// call per file; dates are kept (adb pull -a).
/// </summary>
public static class GalleryBackup
{
    public sealed record Progress(int FilesDone, int Files, long BytesDone, long Bytes, string Current);

    public sealed record Result(int Copied, int Skipped, long Bytes, IReadOnlyList<string> Failures);

    /// <summary>The default folder: Pictures\Streamy backup\PHONE, or a fallback Windows does not protect.</summary>
    public static string DefaultFolder(string deviceName)
    {
        var name = string.Concat(deviceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(SaveFolders.Backups, name.Length == 0 ? "Phone" : name);
    }

    public static async Task<Result> RunAsync(string serial, IReadOnlyList<MediaItem> all, string folder, IProgress<Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var todo = MediaBackup.Plan(all, rel =>
        {
            var f = new FileInfo(Path.Combine(folder, rel));
            return f.Exists ? f.Length : null;
        });
        long total = todo.Sum(i => Math.Max(0, i.Size));
        int skipped = all.Count - todo.Count;
        var failures = new List<string>();
        int done = 0;
        long bytesDone = 0;
        progress.Report(new Progress(0, todo.Count, 0, total, ""));

        // Group by the target folder, then in batches small enough for a command line and a steady progress bar.
        var batches = todo
            .GroupBy(i => Path.GetDirectoryName(MediaBackup.RelativePath(i)) ?? "")
            .SelectMany(g => g.Chunk(40).SelectMany(c => SplitBySize(c, 300L << 20)));

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(folder, Path.GetDirectoryName(MediaBackup.RelativePath(batch[0])) ?? "");
            Directory.CreateDirectory(dir);
            var pull = AdbClient.Default.RunAsync(["pull", "-a", .. batch.Select(i => i.Path), dir], serial, TimeSpan.FromHours(1), ct);
            // Progress inside a batch: the sizes of its files as they grow.
            while (!pull.IsCompleted)
            {
                await Task.WhenAny(pull, Task.Delay(300, CancellationToken.None));
                long partial = batch.Sum(i => { var f = new FileInfo(Path.Combine(folder, MediaBackup.RelativePath(i))); return f.Exists ? Math.Min(f.Length, Math.Max(0, i.Size)) : 0; });
                progress.Report(new Progress(done, todo.Count, bytesDone + partial, total, batch[0].Name));
            }
            AdbResult r;
            try
            {
                r = await pull;
            }
            catch (TimeoutException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (TimeoutException ex)
            {
                failures.Add($"{batch[0].Folder}: {ex.Message}");
                continue;
            }
            foreach (var item in batch)
            {
                var target = new FileInfo(Path.Combine(folder, MediaBackup.RelativePath(item)));
                // adb names the file as on the phone; a name Windows changed (MediaBackup.Safe) needs a rename.
                var pulled = new FileInfo(Path.Combine(dir, item.Name));
                if (!target.Exists && pulled.Exists && pulled.FullName != target.FullName) { pulled.MoveTo(target.FullName, overwrite: true); target.Refresh(); }
                if (target.Exists && (item.Size <= 0 || target.Length == item.Size))
                {
                    done++;
                    bytesDone += Math.Max(0, item.Size);
                }
                else
                {
                    failures.Add(item.Path + (r.Ok ? "" : ": " + r.All.Split('\n').LastOrDefault(l => l.Contains(item.Name, StringComparison.Ordinal))?.Trim()));
                }
            }
            progress.Report(new Progress(done, todo.Count, bytesDone, total, ""));
        }
        return new Result(done, skipped, bytesDone, failures);
    }

    /// <summary>Large videos get batches of their own so the progress moves and a failure costs little.</summary>
    private static IEnumerable<MediaItem[]> SplitBySize(MediaItem[] items, long limit)
    {
        var current = new List<MediaItem>();
        long size = 0;
        foreach (var i in items)
        {
            if (current.Count > 0 && size + i.Size > limit)
            {
                yield return [.. current];
                current.Clear();
                size = 0;
            }
            current.Add(i);
            size += Math.Max(0, i.Size);
        }
        if (current.Count > 0) yield return [.. current];
    }
}
