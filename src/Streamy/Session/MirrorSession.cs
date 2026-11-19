using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Streamy.Core.Protocol;
using Streamy.Core.Server;
using Streamy.Services;
using Streamy.Video;

namespace Streamy.Session;

/// <summary>Counters for one stream, updated by its reader thread.</summary>
public sealed class StreamStats
{
    public long Packets;
    public long Bytes;
    public long KeyFrames;
}

/// <summary>
/// One scrcpy session with one device: pushes the server, opens the forward tunnel, starts the server and
/// connects the video, audio and control sockets in that order. Each session has its own SCID and local port,
/// so several phones (or several sessions on one phone) can run side by side.
/// </summary>
public sealed class MirrorSession : IAsyncDisposable
{
    private readonly AdbClient _adb;
    private readonly string _serial;
    private readonly ServerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private Process? _server;
    private int _localPort;
    private Socket? _video, _audio, _control;
    private readonly List<Task> _readers = [];

    public MirrorSession(AdbClient adb, string serial, ServerOptions options)
    {
        _adb = adb;
        _serial = serial;
        _options = options;
    }

    public string Serial => _serial;
    public string DeviceName { get; private set; } = "";
    public CodecId VideoCodec { get; private set; }
    public CodecId AudioCodec { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public StreamStats VideoStats { get; } = new();
    public StreamStats AudioStats { get; } = new();

    /// <summary>The video decoder, created once the codec is known. Null when video is off.</summary>
    public VideoDecoder? Decoder { get; private set; }

    private readonly PacketMerger _merger = new();

    private readonly System.Text.StringBuilder _log = new();

    /// <summary>Everything scrcpy-server printed so far.</summary>
    public string ServerLog { get { lock (_log) return _log.ToString(); } }

    private void AppendLog(string? line)
    {
        if (line is null) return;
        lock (_log) { if (_log.Length < 64_000) _log.AppendLine(line); }
    }

    /// <summary>Raised from a reader thread when the frame size changes (start, rotation).</summary>
    public event Action<int, int>? SizeChanged;

    /// <summary>Raised from the control reader thread.</summary>
    public event Action<DeviceMessage>? DeviceMessageReceived;

    /// <summary>Raised once when the session ends, with the error if it failed.</summary>
    public event Action<Exception?>? Ended;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _adb.PushAsync(_serial, Dependencies.ServerPath, ScrcpyProtocol.DeviceServerPath, ct);
        _localPort = await _adb.ForwardAsync(_serial, _options.SocketName, ct);
        _server = _adb.Start(["shell", .. _options.ToShellCommand()], _serial);
        // Drain the server's output so its pipe never fills, and keep it for error messages.
        _server.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _server.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        // Forward mode: the first socket receives a dummy byte once the server really listens.
        // Until then adb accepts the connection and closes it, so retry.
        bool first = true;
        if (_options.Video) { _video = await ConnectAsync(first, ct); first = false; }
        if (_options.Audio) { _audio = await ConnectAsync(first, ct); first = false; }
        if (_options.Control) { _control = await ConnectAsync(first, ct); }

        // The sockets are open; the tunnel is no longer needed. Removing it now means even a killed Streamy
        // leaves no forward behind (scrcpy does the same).
        await _adb.RemoveForwardAsync(_serial, _localPort);
        _localPort = 0;

        var firstSocket = _video ?? _audio ?? _control ?? throw new InvalidOperationException("Nothing to stream");
        var name = new byte[ScrcpyProtocol.DeviceNameFieldLength];
        await ReadExactAsync(firstSocket, name, ct);
        DeviceName = DeviceMeta.ParseName(name);

        if (_video is not null)
        {
            VideoCodec = await ReadCodecAsync(_video, ct);
            if (VideoCodec == CodecId.Error) throw new IOException("The phone could not start video capture.\n" + ServerLog);
            Decoder = new VideoDecoder(VideoCodec);
            _readers.Add(Task.Run(() => PumpAsync(_video, VideoStats, isVideo: true, _cts.Token)));
        }
        if (_audio is not null)
        {
            AudioCodec = await ReadCodecAsync(_audio, ct);
            if (AudioCodec is CodecId.Disabled or CodecId.Error) { _audio.Dispose(); _audio = null; } // e.g. Android < 11
            else _readers.Add(Task.Run(() => PumpAsync(_audio, AudioStats, isVideo: false, _cts.Token)));
        }
        if (_control is not null)
        {
            _readers.Add(Task.Run(() => ControlReaderAsync(_control, _cts.Token)));
            _ = Task.Run(() => ControlWriterAsync(_control, _cts.Token));
        }

        _ = Task.WhenAll(_readers).ContinueWith(t => Ended?.Invoke(t.Exception?.InnerException), TaskScheduler.Default);
    }

    /// <summary>Queues one serialized control message. Safe from any thread; messages go out in call order.</summary>
    public void Send(byte[] message)
    {
        if (_control is not null) _outgoing.Writer.TryWrite(message);
    }

    public void Send(IEnumerable<byte[]> messages)
    {
        foreach (var m in messages) Send(m);
    }

    private async Task ControlWriterAsync(Socket s, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
            {
                var mem = msg.AsMemory();
                while (mem.Length > 0) mem = mem[await s.SendAsync(mem, SocketFlags.None, ct)..];
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The session is ending; the readers report why.
        }
    }

    private async Task<Socket> ConnectAsync(bool expectDummyByte, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _localPort), ct);
                if (!expectDummyByte) return s;
                var one = new byte[1];
                if (await s.ReceiveAsync(one, SocketFlags.None, ct) == 1) return s;
            }
            catch (SocketException) { }
            s.Dispose();
            if (_server is { HasExited: true }) throw new IOException("The server on the phone stopped.\n" + ServerLog);
            if (attempt >= 100) throw new TimeoutException("The phone did not answer within 10 seconds.");
            await Task.Delay(100, ct);
        }
    }

    private static async Task<CodecId> ReadCodecAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[4];
        await ReadExactAsync(s, buf, ct);
        return PacketHeader.ReadCodec(buf);
    }

    private async Task PumpAsync(Socket s, StreamStats stats, bool isVideo, CancellationToken ct)
    {
        var header = new byte[ScrcpyProtocol.PacketHeaderSize];
        var payload = new byte[1 << 20];
        while (!ct.IsCancellationRequested)
        {
            await ReadExactAsync(s, header, ct);
            var h = PacketHeader.Parse(header);
            if (h.IsSession)
            {
                if (isVideo)
                {
                    Width = h.Width;
                    Height = h.Height;
                    SizeChanged?.Invoke(h.Width, h.Height);
                }
                continue;
            }
            if (payload.Length < h.PayloadSize) payload = new byte[h.PayloadSize];
            await ReadExactAsync(s, payload.AsMemory(0, h.PayloadSize), ct);
            if (isVideo && Decoder is { } decoder && _merger.Merge(h, payload.AsSpan(0, h.PayloadSize)) is { } data)
                decoder.Decode(data, h.Pts, h.IsKeyFrame);
            Interlocked.Increment(ref stats.Packets);
            Interlocked.Add(ref stats.Bytes, h.PayloadSize);
            if (h.IsKeyFrame) Interlocked.Increment(ref stats.KeyFrames);
        }
    }

    private async Task ControlReaderAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[ScrcpyProtocol.DeviceMessageMaxSize];
        int filled = 0;
        while (!ct.IsCancellationRequested)
        {
            int n = await s.ReceiveAsync(buf.AsMemory(filled), SocketFlags.None, ct);
            if (n == 0) return;
            filled += n;
            int start = 0;
            while (DeviceMessage.TryRead(buf.AsSpan(start, filled - start), out var msg, out int used))
            {
                start += used;
                DeviceMessageReceived?.Invoke(msg!);
            }
            Buffer.BlockCopy(buf, start, buf, 0, filled - start);
            filled -= start;
        }
    }

    private static async Task ReadExactAsync(Socket s, Memory<byte> buf, CancellationToken ct)
    {
        while (buf.Length > 0)
        {
            int n = await s.ReceiveAsync(buf, SocketFlags.None, ct);
            if (n == 0) throw new EndOfStreamException("The phone closed the connection.");
            buf = buf[n..];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        _video?.Dispose();
        _audio?.Dispose();
        _control?.Dispose();
        try { await Task.WhenAll(_readers).WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* readers end with errors when their socket closes */ }
        if (_server is { HasExited: false }) { try { _server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        _server?.Dispose();
        Decoder?.Dispose();
        if (_localPort != 0) { try { await _adb.RemoveForwardAsync(_serial, _localPort); } catch (Exception) { } }
        _cts.Dispose();
    }
}
