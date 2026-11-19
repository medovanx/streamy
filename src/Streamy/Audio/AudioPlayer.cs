using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Streamy.Audio;

/// <summary>
/// Plays decoded samples on the PC's default output through WASAPI (shared mode). Latency is kept low by
/// dropping what is queued whenever more than <see cref="MaxQueued"/> builds up (after a stall or a slow start),
/// instead of letting the sound fall further and further behind the picture.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private static readonly TimeSpan MaxQueued = TimeSpan.FromMilliseconds(200);

    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiPlayer _out;
    private byte[] _bytes = new byte[16384];
    private volatile float _volume = 1f;
    private volatile bool _muted;

    private AudioPlayer(BufferedWaveProvider buffer, WasapiPlayer output)
    {
        _buffer = buffer;
        _out = output;
    }

    /// <summary>Opens the default output device. It follows the default when it changes (headphones plugged in).</summary>
    public static async Task<AudioPlayer> CreateAsync()
    {
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(AudioDecoder.SampleRate, AudioDecoder.Channels), TimeSpan.FromSeconds(1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // play silence on underrun instead of stopping
        };
        var output = await new WasapiPlayerBuilder()
            .WithDefaultDeviceStreamRouting() // follow the default output when it changes (headphones plugged in)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(40)
            .BuildAsync();
        output.Init(buffer);
        output.Play();
        return new AudioPlayer(buffer, output);
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (value) _buffer.ClearBuffer(); }
    }

    /// <summary>0 to 1.</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Queues interleaved float stereo samples. Called on the audio reader thread.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (_muted) return;
        if (_buffer.BufferedDuration > MaxQueued) _buffer.ClearBuffer();

        int bytes = samples.Length * sizeof(float);
        if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];
        var dst = MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, bytes));
        float v = _volume;
        if (v >= 0.999f) samples.CopyTo(dst);
        else for (int i = 0; i < samples.Length; i++) dst[i] = samples[i] * v;
        _buffer.AddSamples(_bytes, 0, bytes);
    }

    public void Dispose()
    {
        _out.Stop();
        _out.Dispose();
    }
}
