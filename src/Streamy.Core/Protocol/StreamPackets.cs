using System.Buffers.Binary;
using System.Text;

namespace Streamy.Core.Protocol;

/// <summary>Codec ids the device sends first on the video and audio sockets (four ASCII bytes, big-endian).</summary>
public enum CodecId : uint
{
    H264 = 0x68323634,
    H265 = 0x68323635,
    Av1 = 0x00617631,
    Vp8 = 0x00767038,
    Vp9 = 0x00767039,
    Opus = 0x6F707573,
    Aac = 0x00616163,
    Flac = 0x666C6163,
    Raw = 0x00726177,

    /// <summary>The device disabled this stream (e.g. it could not capture audio); keep mirroring without it.</summary>
    Disabled = 0,

    /// <summary>A configuration error on the device; the session must stop.</summary>
    Error = 1,
}

/// <summary>
/// The 12-byte header before every packet on the video and audio sockets.
/// A session packet (MSB set, video only) announces a new capture size; a media packet carries codec data.
/// </summary>
public readonly record struct PacketHeader
{
    private const ulong FlagSession = 1UL << 63;
    private const ulong FlagConfig = 1UL << 62;
    private const ulong FlagKeyFrame = 1UL << 61;
    private const ulong PtsMask = FlagKeyFrame - 1;

    public bool IsSession { get; init; }

    // Session packet fields
    public int Width { get; init; }
    public int Height { get; init; }
    public bool ClientResized { get; init; }

    // Media packet fields
    public bool IsConfig { get; init; }
    public bool IsKeyFrame { get; init; }

    /// <summary>Presentation time in microseconds; meaningless for config packets.</summary>
    public ulong Pts { get; init; }

    public int PayloadSize { get; init; }

    public static PacketHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < ScrcpyProtocol.PacketHeaderSize)
            throw new ArgumentException($"A packet header is {ScrcpyProtocol.PacketHeaderSize} bytes", nameof(header));

        if ((header[0] & 0x80) != 0)
        {
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(header);
            return new PacketHeader
            {
                IsSession = true,
                ClientResized = (flags & 1) != 0,
                Width = BinaryPrimitives.ReadInt32BigEndian(header[4..]),
                Height = BinaryPrimitives.ReadInt32BigEndian(header[8..]),
            };
        }

        ulong ptsFlags = BinaryPrimitives.ReadUInt64BigEndian(header);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (size == 0 || size > int.MaxValue) throw new InvalidDataException($"Invalid packet size {size}");
        return new PacketHeader
        {
            IsConfig = (ptsFlags & FlagConfig) != 0,
            IsKeyFrame = (ptsFlags & FlagKeyFrame) != 0,
            Pts = ptsFlags & PtsMask,
            PayloadSize = (int)size,
        };
    }

    public static CodecId ReadCodec(ReadOnlySpan<byte> fourBytes) => (CodecId)BinaryPrimitives.ReadUInt32BigEndian(fourBytes);
}

public static class DeviceMeta
{
    /// <summary>Reads the NUL-padded UTF-8 device name sent on the first socket.</summary>
    public static string ParseName(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }
}
