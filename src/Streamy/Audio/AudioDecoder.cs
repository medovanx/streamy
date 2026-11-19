using FFmpeg.AutoGen;
using Streamy.Core.Protocol;
using Streamy.Video;

namespace Streamy.Audio;

/// <summary>
/// Decodes the phone's audio stream (Opus by default; AAC, FLAC or raw PCM if chosen) to interleaved
/// 32-bit float stereo at 48 kHz, the format scrcpy-server captures in. The codec configuration packet
/// (OpusHead, AudioSpecificConfig, FLAC STREAMINFO) becomes the decoder's extradata.
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly CodecId _codec;
    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private float[] _out = new float[4096];

    /// <summary>Raised on the reader thread with interleaved float samples (valid only during the call).</summary>
    public event Action<ReadOnlySpan<float>>? Samples;

    public AudioDecoder(CodecId codec)
    {
        VideoDecoder.EnsureFfmpeg();
        _codec = codec;
        if (codec is not (CodecId.Opus or CodecId.Aac or CodecId.Flac or CodecId.Raw))
            throw new NotSupportedException($"Streamy cannot play {codec} audio.");
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Feeds one packet from the audio socket.</summary>
    public void Feed(PacketHeader header, ReadOnlySpan<byte> payload)
    {
        if (_codec == CodecId.Raw)
        {
            if (!header.IsConfig) EmitS16(payload);
            return;
        }
        if (header.IsConfig)
        {
            Open(payload);
            return;
        }
        if (_ctx is null) Open([]); // no config packet: open without extradata

        fixed (byte* p = payload)
        {
            _packet->data = p;
            _packet->size = payload.Length;
            _packet->pts = (long)header.Pts;
            int r = ffmpeg.avcodec_send_packet(_ctx, _packet);
            _packet->data = null;
            _packet->size = 0;
            if (r < 0 && r != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return;
        }
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) >= 0)
        {
            EmitFrame(_frame);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private void Open(ReadOnlySpan<byte> extradata)
    {
        if (_ctx is not null) fixed (AVCodecContext** c = &_ctx) ffmpeg.avcodec_free_context(c);
        var id = _codec switch
        {
            CodecId.Opus => AVCodecID.AV_CODEC_ID_OPUS,
            CodecId.Aac => AVCodecID.AV_CODEC_ID_AAC,
            _ => AVCodecID.AV_CODEC_ID_FLAC,
        };
        var decoder = ffmpeg.avcodec_find_decoder(id);
        if (decoder is null) throw new NotSupportedException($"This FFmpeg build has no {_codec} decoder.");
        _ctx = ffmpeg.avcodec_alloc_context3(decoder);
        _ctx->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, Channels);
        if (extradata.Length > 0)
        {
            _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(extradata.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            extradata.CopyTo(new Span<byte>(_ctx->extradata, extradata.Length));
            _ctx->extradata_size = extradata.Length;
        }
        if (ffmpeg.avcodec_open2(_ctx, decoder, null) < 0) throw new InvalidOperationException($"FFmpeg could not open the {_codec} decoder.");
    }

    private void EmitFrame(AVFrame* f)
    {
        int n = f->nb_samples, ch = f->ch_layout.nb_channels;
        if (n <= 0 || ch <= 0) return;
        if (_out.Length < n * Channels) _out = new float[n * Channels * 2];
        var fmt = (AVSampleFormat)f->format;
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < Channels; c++)
            {
                int sc = Math.Min(c, ch - 1); // mono goes to both ears
                _out[i * Channels + c] = fmt switch
                {
                    AVSampleFormat.AV_SAMPLE_FMT_FLTP => ((float*)f->extended_data[sc])[i],
                    AVSampleFormat.AV_SAMPLE_FMT_FLT => ((float*)f->extended_data[0])[i * ch + sc],
                    AVSampleFormat.AV_SAMPLE_FMT_S16P => ((short*)f->extended_data[sc])[i] / 32768f,
                    AVSampleFormat.AV_SAMPLE_FMT_S16 => ((short*)f->extended_data[0])[i * ch + sc] / 32768f,
                    AVSampleFormat.AV_SAMPLE_FMT_S32P => ((int*)f->extended_data[sc])[i] / 2147483648f,
                    AVSampleFormat.AV_SAMPLE_FMT_S32 => ((int*)f->extended_data[0])[i * ch + sc] / 2147483648f,
                    _ => 0f,
                };
            }
        }
        Samples?.Invoke(_out.AsSpan(0, n * Channels));
    }

    private void EmitS16(ReadOnlySpan<byte> pcm)
    {
        int count = pcm.Length / 2;
        if (_out.Length < count) _out = new float[count * 2];
        for (int i = 0; i < count; i++) _out[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
        Samples?.Invoke(_out.AsSpan(0, count - count % Channels));
    }

    public void Dispose()
    {
        if (_ctx is not null) fixed (AVCodecContext** c = &_ctx) ffmpeg.avcodec_free_context(c);
        fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
        fixed (AVFrame** f = &_frame) ffmpeg.av_frame_free(f);
    }
}
