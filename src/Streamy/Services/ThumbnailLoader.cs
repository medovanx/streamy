using System.Drawing.Drawing2D;
using Streamy.Core.Media;

namespace Streamy.Services;

/// <summary>
/// Square thumbnails for the gallery. The phone makes them (so HEIC photos and videos work without codecs on the
/// PC); each one costs a "content read" on the phone, about a second of Java start-up, so several run at once and
/// what is on screen right now always goes first. Every thumbnail is kept on disk under
/// %LOCALAPPDATA%\Streamy\thumbs\&lt;phone&gt;, keyed by the item's id and modification time, so a photo is only
/// fetched once; a few hundred stay decoded in memory.
/// </summary>
public sealed class ThumbnailLoader : IDisposable
{
    private const int Workers = 8;
    private const int MemoryLimit = 500;

    private readonly string _serial;
    private readonly int _size;
    private readonly string _cacheDir;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Bitmap> _memory = [];
    private readonly LinkedList<string> _recent = new(); // least recently used first
    private readonly LinkedList<MediaItem> _queue = new(); // next to fetch first
    private readonly HashSet<string> _queued = [];
    private readonly HashSet<string> _inFlight = [];
    private readonly HashSet<string> _failed = [];
    private readonly CancellationTokenSource _cts = new();
    private int _running;

    /// <summary>Raised on a worker thread when a thumbnail is ready.</summary>
    public event Action? Loaded;

    /// <param name="size">The square tile size in pixels; thumbnails are cropped to fill it.</param>
    public ThumbnailLoader(string serial, int size)
    {
        _serial = serial;
        _size = size;
        string safe = string.Concat(serial.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Streamy", "thumbs", safe);
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>The thumbnail if it is ready; otherwise null (ask with <see cref="Want"/>).</summary>
    public Bitmap? Get(MediaItem item)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(item.CacheKey, out var bmp)) return null;
            _recent.Remove(item.CacheKey);
            _recent.AddLast(item.CacheKey);
            return bmp;
        }
    }

    public bool Failed(MediaItem item)
    {
        lock (_gate) return _failed.Contains(item.CacheKey);
    }

    /// <summary>
    /// Replaces the queue with these items, in this order (visible ones first, then a little ahead). Items that
    /// scrolled away are dropped from the queue, so fast scrolling never builds a backlog.
    /// </summary>
    public void Want(IEnumerable<MediaItem> items)
    {
        lock (_gate)
        {
            _queue.Clear();
            _queued.Clear();
            foreach (var item in items)
            {
                var key = item.CacheKey;
                if (_memory.ContainsKey(key) || _failed.Contains(key) || _inFlight.Contains(key) || !_queued.Add(key)) continue;
                _queue.AddLast(item);
            }
            while (_running < Workers && _queue.Count > 0)
            {
                _running++;
                _ = Task.Run(WorkAsync);
            }
        }
    }

    private async Task WorkAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            MediaItem item;
            lock (_gate)
            {
                if (_queue.First is not { } first) { _running--; return; }
                item = first.Value;
                _queue.RemoveFirst();
                _queued.Remove(item.CacheKey);
                _inFlight.Add(item.CacheKey);
            }
            Bitmap? bmp = null;
            try
            {
                bmp = await LoadAsync(item);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Info($"Thumbnail {item.Id}: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                lock (_gate) { _running--; _inFlight.Remove(item.CacheKey); }
                return;
            }
            lock (_gate)
            {
                _inFlight.Remove(item.CacheKey);
                if (bmp is null) _failed.Add(item.CacheKey);
                else Remember(item.CacheKey, bmp);
            }
            Loaded?.Invoke();
        }
        lock (_gate) _running--;
    }

    private async Task<Bitmap?> LoadAsync(MediaItem item)
    {
        string file = Path.Combine(_cacheDir, item.CacheKey);
        byte[] bytes;
        if (File.Exists(file))
        {
            bytes = await File.ReadAllBytesAsync(file, _cts.Token);
        }
        else
        {
            bytes = await AdbClient.Default.ReadContentAsync(_serial, item.ThumbnailUri, _cts.Token);
            if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null; // not a JPEG: an error message
            await File.WriteAllBytesAsync(file, bytes, _cts.Token);
        }
        using var ms = new MemoryStream(bytes);
        using var src = new Bitmap(ms);
        return CropToSquare(src, _size);
    }

    /// <summary>Scales the picture to cover a square tile and crops the middle, like phone galleries do.</summary>
    private static Bitmap CropToSquare(Image src, int size)
    {
        var dst = new Bitmap(size, size);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Max((float)size / src.Width, (float)size / src.Height);
        float w = src.Width * scale, h = src.Height * scale;
        g.DrawImage(src, (size - w) / 2, (size - h) / 2, w, h);
        return dst;
    }

    private void Remember(string key, Bitmap bmp)
    {
        _memory[key] = bmp;
        _recent.AddLast(key);
        while (_memory.Count > MemoryLimit && _recent.First is { } oldest)
        {
            _recent.RemoveFirst();
            // Not disposed here: the UI thread may be painting it right now. The GC frees it once unused.
            _memory.Remove(oldest.Value);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate)
        {
            foreach (var b in _memory.Values) b.Dispose();
            _memory.Clear();
        }
    }
}
