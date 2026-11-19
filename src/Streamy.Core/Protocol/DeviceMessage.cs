using System.Buffers.Binary;
using System.Text;

namespace Streamy.Core.Protocol;

public enum DeviceMessageType : byte
{
    Clipboard,
    AckClipboard,
    UhidOutput,
}

/// <summary>A message from the device on the control socket.</summary>
public abstract record DeviceMessage
{
    /// <summary>The device clipboard changed (or was requested); <see cref="Text"/> is its new content.</summary>
    public sealed record Clipboard(string Text) : DeviceMessage;

    /// <summary>The device applied a SetClipboard with this sequence number.</summary>
    public sealed record AckClipboard(ulong Sequence) : DeviceMessage;

    public sealed record UhidOutput(ushort Id, byte[] Data) : DeviceMessage;

    /// <summary>
    /// Reads one message from the start of <paramref name="buf"/>. Returns false when more bytes are needed.
    /// Throws <see cref="InvalidDataException"/> on an unknown type or an oversized message.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> buf, out DeviceMessage? message, out int consumed)
    {
        message = null;
        consumed = 0;
        if (buf.IsEmpty) return false;

        switch ((DeviceMessageType)buf[0])
        {
            case DeviceMessageType.Clipboard:
            {
                if (buf.Length < 5) return false;
                uint len = BinaryPrimitives.ReadUInt32BigEndian(buf[1..]);
                if (len > ScrcpyProtocol.DeviceMessageTextMaxLength) throw new InvalidDataException($"Clipboard text of {len} bytes is too long");
                if (buf.Length < 5 + len) return false;
                message = new Clipboard(Encoding.UTF8.GetString(buf.Slice(5, (int)len)));
                consumed = 5 + (int)len;
                return true;
            }
            case DeviceMessageType.AckClipboard:
            {
                if (buf.Length < 9) return false;
                message = new AckClipboard(BinaryPrimitives.ReadUInt64BigEndian(buf[1..]));
                consumed = 9;
                return true;
            }
            case DeviceMessageType.UhidOutput:
            {
                if (buf.Length < 5) return false;
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(buf[1..]);
                ushort size = BinaryPrimitives.ReadUInt16BigEndian(buf[3..]);
                if (buf.Length < 5 + size) return false;
                message = new UhidOutput(id, buf.Slice(5, size).ToArray());
                consumed = 5 + size;
                return true;
            }
            default:
                throw new InvalidDataException($"Unknown device message type {buf[0]}");
        }
    }
}
