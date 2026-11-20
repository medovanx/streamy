using System.Text.Json;
using Streamy.Core.Media;

namespace Streamy.Services;

/// <summary>
/// Where each photo and video was taken, for one phone. Read once from a small slice of every file (EXIF GPS,
/// MP4 ©xyz) in batches over adb, then kept in %LOCALAPPDATA%\Streamy\locations\&lt;phone&gt;.json keyed by item id
/// and modification time, so later visits only read new items. Place names come from the offline city list.
/// </summary>
public sealed class LocationIndex
{
    private const int Batch = 40;

    private readonly string _serial;
    private readonly string _file;
    private readonly Lock _gate = new();
    private Dictionary<string, double[]?> _points = new(StringComparer.Ordinal); // cache key → [lat, lon] or null (none)

    public LocationIndex(string serial)
    {
        _serial = serial;
        string safe = string.Concat(serial.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streamy", "locations");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, safe + ".json");
        try
        {
            if (File.Exists(_file))
                _points = JsonSerializer.Deserialize<Dictionary<string, double[]?>>(File.ReadAllText(_file)) ?? _points;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Info("Location cache unreadable, starting over: " + ex.Message);
        }
    }

    private static string Key(MediaItem i) => GeoTags.Key(i) + "_" + i.Modified;

    public GeoPoint? Point(MediaItem item)
    {
        lock (_gate)
            return _points.TryGetValue(Key(item), out var p) && p is { Length: 2 } ? new GeoPoint(p[0], p[1]) : null;
    }

    /// <summary>The place name ("Alexandria, Egypt"), or null when the item has no location or it is unknown yet.</summary>
    public string? Place(MediaItem item) => Point(item) is { } p ? Geocoder.Default.Name(p) : null;

    public bool Known(MediaItem item)
    {
        lock (_gate) return _points.ContainsKey(Key(item));
    }

    /// <summary>
    /// Reads locations for every item not seen before. Reports (done, total) after each batch; saves to disk as it
    /// goes so an interrupted scan resumes where it stopped.
    /// </summary>
    public async Task ScanAsync(IReadOnlyList<MediaItem> items, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var todo = items.Where(i => !Known(i)).ToList();
        // Items that cannot carry a location are settled without reading them.
        foreach (var i in todo.Where(i => !GeoTags.MayHaveLocation(i)))
            lock (_gate) _points[Key(i)] = null;
        todo = todo.Where(GeoTags.MayHaveLocation).ToList();
        int done = 0, batches = 0;
        progress?.Report((0, todo.Count));
        var batchList = todo.Chunk(Batch).ToList();
        // A few batches at once: the phone reads files while the previous batch is still crossing the cable.
        await Parallel.ForEachAsync(batchList, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (batch, token) =>
        {
            await ReadBatchAsync(batch, token);
            int d = Interlocked.Add(ref done, batch.Length);
            progress?.Report((d, todo.Count));
            if (Interlocked.Increment(ref batches) % 25 == 0) Save();
        });
        Save();
    }

    private async Task ReadBatchAsync(MediaItem[] batch, CancellationToken ct)
    {
        foreach (bool exact in new[] { false, true })
        {
            try
            {
                var bytes = await AdbClient.Default.ExecOutAsync(_serial, GeoTags.BatchCommand(batch, exact), TimeSpan.FromMinutes(2), ct);
                var (found, complete) = GeoTags.ParseBatch(bytes, batch.Length);
                lock (_gate)
                {
                    foreach (var item in batch)
                    {
                        if (!found.TryGetValue(GeoTags.Key(item), out var p)) continue;
                        _points[Key(item)] = p is { } g ? [Math.Round(g.Latitude, 6), Math.Round(g.Longitude, 6)] : null;
                    }
                }
                if (complete) return;
                // A file changed size since it was indexed: read the batch again, asking the phone for every size.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Info("Location batch timed out; it is read again next time.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info("Location batch: " + ex.Message);
                return;
            }
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_points);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException ex) { Log.Info("Location cache not saved: " + ex.Message); }
    }
}
