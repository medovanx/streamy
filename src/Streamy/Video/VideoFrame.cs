namespace Streamy.Video;

/// <summary>YUV colour matrix and range of a frame, from the stream's metadata.</summary>
public readonly record struct ColorInfo(bool Bt709, bool FullRange);

/// <summary>A decoded YUV 4:2:0 picture in three tightly packed planes, reused from frame to frame.</summary>
public sealed class VideoFrame
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ChromaWidth => (Width + 1) / 2;
    public int ChromaHeight => (Height + 1) / 2;
    public ColorInfo Color { get; set; }
    public byte[] Y { get; private set; } = [];
    public byte[] U { get; private set; } = [];
    public byte[] V { get; private set; } = [];

    public void EnsureSize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Y = new byte[width * height];
        U = new byte[ChromaWidth * ChromaHeight];
        V = new byte[ChromaWidth * ChromaHeight];
    }
}

/// <summary>
/// Triple buffer between the decoder thread and the render thread. The decoder writes into a frame the renderer
/// never holds, so neither waits for the other; the renderer always gets the newest picture and older ones are dropped.
/// </summary>
public sealed class FrameExchange
{
    private readonly Lock _gate = new();
    private readonly VideoFrame[] _frames = [new(), new(), new()];
    private int _write = 0, _ready = 1, _read = 2;
    private bool _fresh;

    /// <summary>The frame the decoder fills next. Only the decoder thread may touch it.</summary>
    public VideoFrame Writable => _frames[_write];

    /// <summary>Makes the frame just written the newest one.</summary>
    public void Publish()
    {
        lock (_gate)
        {
            (_write, _ready) = (_ready, _write);
            _fresh = true;
        }
    }

    /// <summary>A copy of the newest decoded picture (for screenshots), or null before the first frame.</summary>
    public VideoFrame? CopyLatest()
    {
        lock (_gate) // Publish waits meanwhile, so the decoder never writes into the frame being copied
        {
            var src = _fresh ? _frames[_ready] : _frames[_read];
            if (src.Width == 0) src = _frames[_ready].Width > 0 ? _frames[_ready] : _frames[_read];
            if (src.Width == 0) return null;
            var copy = new VideoFrame { Color = src.Color };
            copy.EnsureSize(src.Width, src.Height);
            src.Y.CopyTo(copy.Y, 0);
            src.U.CopyTo(copy.U, 0);
            src.V.CopyTo(copy.V, 0);
            return copy;
        }
    }

    /// <summary>The newest frame if one arrived since the last call, else null. Valid until the next call.</summary>
    public VideoFrame? Take()
    {
        lock (_gate)
        {
            if (!_fresh) return null;
            (_read, _ready) = (_ready, _read);
            _fresh = false;
            return _frames[_read];
        }
    }
}
