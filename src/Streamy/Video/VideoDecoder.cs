using FFmpeg.AutoGen;
using Streamy.Core.Protocol;
using Streamy.Services;

namespace Streamy.Video;

/// <summary>
/// Decodes H.264, H.265 or AV1 packets with FFmpeg (software, low-delay) into <see cref="FrameExchange"/> frames.
/// Runs on the session's video reader thread.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private static readonly Lock InitGate = new();
    private static bool _initialized;

    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;

    public FrameExchange Frames { get; } = new();

    /// <summary>Raised on the decoder thread after each published frame.</summary>
    public event Action? FrameDecoded;

    public long DecodedFrames { get; private set; }

    public VideoDecoder(CodecId codec)
    {
        EnsureFfmpeg();
        var id = codec switch
        {
            CodecId.H264 => AVCodecID.AV_CODEC_ID_H264,
            CodecId.H265 => AVCodecID.AV_CODEC_ID_HEVC,
            CodecId.Av1 => AVCodecID.AV_CODEC_ID_AV1,
            _ => throw new NotSupportedException($"Streamy cannot decode {codec} video."),
        };
        var decoder = ffmpeg.avcodec_find_decoder(id);
        if (decoder is null) throw new NotSupportedException($"This FFmpeg build has no {codec} decoder.");

        _ctx = ffmpeg.avcodec_alloc_context3(decoder);
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        Check(ffmpeg.avcodec_open2(_ctx, decoder, null), "open the decoder");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Loads the bundled FFmpeg DLLs once per process.</summary>
    public static void EnsureFfmpeg()
    {
        lock (InitGate)
        {
            if (_initialized) return;
            ffmpeg.RootPath = Dependencies.Directory;
            uint version = ffmpeg.avcodec_version();
            if (version >> 16 != ffmpeg.LIBAVCODEC_VERSION_MAJOR)
                throw new DllNotFoundException($"avcodec {version >> 16} found, {ffmpeg.LIBAVCODEC_VERSION_MAJOR} expected.");
            _initialized = true;
        }
    }

    /// <summary>Decodes one complete packet (config already merged in front).</summary>
    public void Decode(byte[] data, ulong pts, bool keyFrame)
    {
        fixed (byte* p = data)
        {
            _packet->data = p;
            _packet->size = data.Length;
            _packet->pts = (long)pts;
            _packet->dts = (long)pts;
            _packet->flags = keyFrame ? ffmpeg.AV_PKT_FLAG_KEY : 0;
            int r = ffmpeg.avcodec_send_packet(_ctx, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return; // a damaged packet; the next key frame recovers
        }

        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) >= 0)
        {
            CopyOut(_frame);
            ffmpeg.av_frame_unref(_frame);
            DecodedFrames++;
            Frames.Publish();
            FrameDecoded?.Invoke();
        }
    }

    private void CopyOut(AVFrame* f)
    {
        var fmt = (AVPixelFormat)f->format;
        if (fmt is not (AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P))
            throw new NotSupportedException($"Unexpected decoder output {fmt}.");

        var dst = Frames.Writable;
        dst.EnsureSize(f->width, f->height);
        dst.Color = new ColorInfo(
            Bt709: f->colorspace == AVColorSpace.AVCOL_SPC_BT709,
            FullRange: f->color_range == AVColorRange.AVCOL_RANGE_JPEG || fmt == AVPixelFormat.AV_PIX_FMT_YUVJ420P);
        CopyPlane(f->data[0], f->linesize[0], dst.Y, dst.Width, dst.Height);
        CopyPlane(f->data[1], f->linesize[1], dst.U, dst.ChromaWidth, dst.ChromaHeight);
        CopyPlane(f->data[2], f->linesize[2], dst.V, dst.ChromaWidth, dst.ChromaHeight);
    }

    private static void CopyPlane(byte* src, int stride, byte[] dst, int width, int height)
    {
        fixed (byte* d = dst)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * stride, d + (long)y * width, width, width);
        }
    }

    private static void Check(int r, string what)
    {
        if (r >= 0) return;
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(r, buf, 256);
        throw new InvalidOperationException($"FFmpeg could not {what}: {new string((sbyte*)buf)}");
    }

    public void Dispose()
    {
        fixed (AVCodecContext** c = &_ctx) ffmpeg.avcodec_free_context(c);
        fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
        fixed (AVFrame** f = &_frame) ffmpeg.av_frame_free(f);
    }
}
