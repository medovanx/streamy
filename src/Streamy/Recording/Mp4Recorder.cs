using FFmpeg.AutoGen;
using Streamy.Core.Protocol;
using Streamy.Video;

namespace Streamy.Recording;

/// <summary>
/// Writes the phone's video and audio packets into an MP4 (or MKV, by extension) without re-encoding, as
/// scrcpy's recorder does. The file starts at the first video key frame; timestamps are made relative to it.
/// Thread-safe: video and audio arrive from different reader threads.
/// </summary>
public sealed unsafe class Mp4Recorder : IDisposable
{
    private static readonly AVRational Microseconds = new() { num = 1, den = 1_000_000 };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly CodecId _videoCodec;
    private readonly CodecId _audioCodec; // Disabled when there is no audio
    private readonly int _width, _height;
    private byte[]? _videoConfig, _audioConfig;
    private byte[]? _pendingVideoConfig; // a new configuration after the start (rotation): sent in-band
    private AVFormatContext* _ctx;
    private AVStream* _videoStream, _audioStream;
    private AVPacket* _packet;
    private bool _started, _closed;
    private ulong _basePts;

    public string Path => _path;
    public DateTime StartedAt { get; } = DateTime.Now;

    public Mp4Recorder(string path, CodecId videoCodec, int width, int height, byte[]? videoConfig, CodecId audioCodec, byte[]? audioConfig)
    {
        VideoDecoder.EnsureFfmpeg();
        _path = path;
        _videoCodec = videoCodec;
        _audioCodec = audioCodec is CodecId.Opus or CodecId.Aac or CodecId.Flac ? audioCodec : CodecId.Disabled;
        _width = width;
        _height = height;
        _videoConfig = videoConfig;
        _audioConfig = audioConfig;
        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>Feeds one packet as it came from the phone (config packets included, not merged).</summary>
    public void Write(bool isVideo, PacketHeader h, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (h.IsConfig)
            {
                if (!isVideo) _audioConfig ??= payload.ToArray();
                else if (_started) _pendingVideoConfig = payload.ToArray();
                else _videoConfig = payload.ToArray();
                return;
            }
            if (!_started)
            {
                // Wait for a video key frame with its configuration (and the audio configuration, if any).
                if (!isVideo || !h.IsKeyFrame || _videoConfig is null) return;
                if (_audioCodec != CodecId.Disabled && _audioConfig is null) return;
                Open();
                _basePts = h.Pts;
                _started = true;
            }
            if (h.Pts < _basePts) return; // audio from before the first frame

            var stream = isVideo ? _videoStream : _audioStream;
            if (stream is null) return;

            // After a rotation the encoder restarts with new parameter sets: put them in front of the next frame.
            byte[]? prefix = isVideo ? _pendingVideoConfig : null;
            if (isVideo) _pendingVideoConfig = null;
            int size = (prefix?.Length ?? 0) + payload.Length;
            if (ffmpeg.av_new_packet(_packet, size) < 0) return;
            var dst = new Span<byte>(_packet->data, size);
            prefix?.CopyTo(dst);
            payload.CopyTo(dst[(prefix?.Length ?? 0)..]);
            long pts = (long)(h.Pts - _basePts);
            _packet->pts = pts;
            _packet->dts = pts;
            _packet->flags = h.IsKeyFrame ? ffmpeg.AV_PKT_FLAG_KEY : 0;
            _packet->stream_index = stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, Microseconds, stream->time_base);
            ffmpeg.av_interleaved_write_frame(_ctx, _packet); // takes ownership of the packet's data
        }
    }

    private void Open()
    {
        string format = _path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "matroska" : "mp4";
        AVFormatContext* ctx = null;
        Check(ffmpeg.avformat_alloc_output_context2(&ctx, null, format, _path), "create the recording");
        _ctx = ctx;

        _videoStream = ffmpeg.avformat_new_stream(_ctx, null);
        var vp = _videoStream->codecpar;
        vp->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        vp->codec_id = _videoCodec switch
        {
            CodecId.H265 => AVCodecID.AV_CODEC_ID_HEVC,
            CodecId.Av1 => AVCodecID.AV_CODEC_ID_AV1,
            _ => AVCodecID.AV_CODEC_ID_H264,
        };
        vp->width = _width;
        vp->height = _height;
        SetExtradata(vp, _videoConfig!);
        _videoStream->time_base = Microseconds;

        if (_audioCodec != CodecId.Disabled)
        {
            _audioStream = ffmpeg.avformat_new_stream(_ctx, null);
            var ap = _audioStream->codecpar;
            ap->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            ap->codec_id = _audioCodec switch
            {
                CodecId.Aac => AVCodecID.AV_CODEC_ID_AAC,
                CodecId.Flac => AVCodecID.AV_CODEC_ID_FLAC,
                _ => AVCodecID.AV_CODEC_ID_OPUS,
            };
            ap->sample_rate = 48000;
            ffmpeg.av_channel_layout_default(&ap->ch_layout, 2);
            SetExtradata(ap, _audioConfig!);
            _audioStream->time_base = Microseconds;
        }

        Check(ffmpeg.avio_open(&_ctx->pb, _path, ffmpeg.AVIO_FLAG_WRITE), "create the file");
        Check(ffmpeg.avformat_write_header(_ctx, null), "start the recording");
    }

    private static void SetExtradata(AVCodecParameters* par, byte[] data)
    {
        par->extradata = (byte*)ffmpeg.av_mallocz((ulong)(data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        data.CopyTo(new Span<byte>(par->extradata, data.Length));
        par->extradata_size = data.Length;
    }

    /// <summary>True once the first key frame was written (the file exists and has content).</summary>
    public bool HasStarted
    {
        get { lock (_gate) return _started; }
    }

    /// <summary>Finishes the file. Safe to call twice.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_ctx is not null)
            {
                if (_started) ffmpeg.av_write_trailer(_ctx);
                if (_ctx->pb is not null) ffmpeg.avio_closep(&_ctx->pb);
                ffmpeg.avformat_free_context(_ctx);
                _ctx = null;
            }
            fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
        }
    }

    private static void Check(int r, string what)
    {
        if (r >= 0) return;
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(r, buf, 256);
        throw new IOException($"Could not {what}: {new string((sbyte*)buf)}");
    }
}
