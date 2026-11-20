using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;

namespace Streamy.Services;

/// <summary>
/// OpenStreetMap's standard map tiles, as their tile usage policy asks: a User-Agent naming the app, only the
/// tiles on screen, two downloads at a time, and a disk cache (%LOCALAPPDATA%\Streamy\tiles, refreshed after 30
/// days) so a tile is fetched once. Only used after the user agrees to the map's privacy note.
/// </summary>
public sealed class MapTiles : IDisposable
{
    public const int MaxZoom = 19;
    public const string Attribution = "© OpenStreetMap contributors";

    private static readonly HttpClient Http = CreateClient();
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streamy", "tiles");

    private readonly ConcurrentDictionary<(int Z, int X, int Y), Bitmap?> _memory = new();
    private readonly ConcurrentDictionary<(int Z, int X, int Y), byte> _pending = new();
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly LinkedList<(int, int, int)> _order = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Raised (on a worker thread) when a tile became available.</summary>
    public event Action? Arrived;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Streamy/{Updater.CurrentText} (+https://github.com/medovanx/streamy)");
        return c;
    }

    /// <summary>The tile if it is ready; otherwise null, and it is fetched (from disk or the web) in the background.</summary>
    public Bitmap? Get(int z, int x, int y)
    {
        int n = 1 << z;
        if (y < 0 || y >= n) return null;
        x = ((x % n) + n) % n; // the world wraps sideways
        var key = (z, x, y);
        if (_memory.TryGetValue(key, out var bmp)) return bmp;
        if (_pending.TryAdd(key, 0)) _ = LoadAsync(key);
        return null;
    }

    private async Task LoadAsync((int Z, int X, int Y) key)
    {
        try
        {
            var file = Path.Combine(Folder, key.Z.ToString(CultureInfo.InvariantCulture), key.X.ToString(CultureInfo.InvariantCulture), key.Y.ToString(CultureInfo.InvariantCulture) + ".png");
            byte[]? data = null;
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-30))
            {
                data = await File.ReadAllBytesAsync(file);
            }
            else
            {
                await _downloads.WaitAsync();
                try
                {
                    if (_disposed) return;
                    data = await Http.GetByteArrayAsync(string.Create(CultureInfo.InvariantCulture, $"https://tile.openstreetmap.org/{key.Z}/{key.X}/{key.Y}.png"));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, data);
                }
                finally
                {
                    _downloads.Release();
                }
            }
            using var ms = new MemoryStream(data);
            var bmp = new Bitmap(ms);
            Remember(key, bmp);
            Arrived?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or ArgumentException)
        {
            Log.Info($"map tile {key}: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>Keeps the 300 most recent tiles in memory.</summary>
    private void Remember((int, int, int) key, Bitmap bmp)
    {
        lock (_gate)
        {
            _memory[key] = bmp;
            _order.AddLast(key);
            while (_order.Count > 300)
            {
                var old = _order.First!.Value;
                _order.RemoveFirst();
                // Not disposed: a paint may be drawing it right now; the GC frees it.
                _memory.TryRemove(old, out _);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
