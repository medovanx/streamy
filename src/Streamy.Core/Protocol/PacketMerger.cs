namespace Streamy.Core.Protocol;

/// <summary>
/// MediaCodec sends codec configuration (H.264 SPS/PPS, H.265 VPS/SPS/PPS) as a separate config packet.
/// Decoders want it in front of the next frame, so it is held and prepended to the following media packet,
/// as scrcpy's own client does (app/src/packet_merger.c).
/// </summary>
public sealed class PacketMerger
{
    private byte[]? _config;

    /// <summary>
    /// Feeds one packet. Returns the bytes to decode, or null for a config packet (kept for the next call).
    /// </summary>
    public byte[]? Merge(PacketHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.IsConfig)
        {
            _config = payload.ToArray();
            return null;
        }
        if (_config is null) return payload.ToArray();

        var merged = new byte[_config.Length + payload.Length];
        _config.CopyTo(merged, 0);
        payload.CopyTo(merged.AsSpan(_config.Length));
        _config = null;
        return merged;
    }
}
