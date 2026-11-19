using System.Buffers.Binary;
using System.Text;
using Streamy.Core.Input;

namespace Streamy.Core.Protocol;

/// <summary>Control message types sent from the PC to the device (scrcpy 5.0.1 order).</summary>
public enum ControlMessageType : byte
{
    InjectKeycode,
    InjectText,
    InjectTouchEvent,
    InjectScrollEvent,
    BackOrScreenOn,
    ExpandNotificationPanel,
    ExpandSettingsPanel,
    CollapsePanels,
    GetClipboard,
    SetClipboard,
    SetDisplayPower,
    RotateDevice,
    UhidCreate,
    UhidInput,
    UhidDestroy,
    OpenHardKeyboardSettings,
    StartApp,
    ResetVideo,
    CameraSetTorch,
    CameraZoomIn,
    CameraZoomOut,
    ResizeDisplay,
    ScanFile,
}

/// <summary>Which key the device simulates before reading its clipboard.</summary>
public enum CopyKey : byte
{
    None = 0,
    Copy = 1,
    Cut = 2,
}

/// <summary>A point in video frame coordinates plus the frame size it refers to.</summary>
public readonly record struct DevicePosition(int X, int Y, ushort FrameWidth, ushort FrameHeight);

/// <summary>
/// Serializes control messages byte for byte like scrcpy's control_msg.c. Every multi-byte field is big-endian.
/// The unit tests port scrcpy's own test vectors (app/tests/test_control_msg_serialize.c).
/// </summary>
public static class ControlMessage
{
    public static byte[] InjectKeycode(AndroidKeyAction action, AndroidKeycode keycode, uint repeat, AndroidMetaState metaState)
    {
        var buf = new byte[14];
        buf[0] = (byte)ControlMessageType.InjectKeycode;
        buf[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(2), (int)keycode);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(6), repeat);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(10), (uint)metaState);
        return buf;
    }

    public static byte[] InjectText(string text) =>
        WithString(ControlMessageType.InjectText, [], text, ScrcpyProtocol.InjectTextMaxLength);

    public static byte[] InjectTouch(AndroidMotionAction action, ulong pointerId, DevicePosition position, float pressure,
        AndroidMotionButtons actionButton, AndroidMotionButtons buttons)
    {
        var buf = new byte[32];
        buf[0] = (byte)ControlMessageType.InjectTouchEvent;
        buf[1] = (byte)action;
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(2), pointerId);
        WritePosition(buf.AsSpan(10), position);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(22), FloatToU16Fixed(pressure));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24), (uint)actionButton);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28), (uint)buttons);
        return buf;
    }

    /// <param name="hScroll">Horizontal scroll in [-16, 16]; values outside are clamped.</param>
    /// <param name="vScroll">Vertical scroll in [-16, 16]; values outside are clamped.</param>
    public static byte[] InjectScroll(DevicePosition position, float hScroll, float vScroll, AndroidMotionButtons buttons)
    {
        var buf = new byte[21];
        buf[0] = (byte)ControlMessageType.InjectScrollEvent;
        WritePosition(buf.AsSpan(1), position);
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(13), FloatToI16Fixed(Math.Clamp(hScroll / 16, -1f, 1f)));
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(15), FloatToI16Fixed(Math.Clamp(vScroll / 16, -1f, 1f)));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(17), (uint)buttons);
        return buf;
    }

    /// <summary>Back if the screen is on, otherwise wakes it.</summary>
    public static byte[] BackOrScreenOn(AndroidKeyAction action) => [(byte)ControlMessageType.BackOrScreenOn, (byte)action];

    public static byte[] ExpandNotificationPanel() => [(byte)ControlMessageType.ExpandNotificationPanel];
    public static byte[] ExpandSettingsPanel() => [(byte)ControlMessageType.ExpandSettingsPanel];
    public static byte[] CollapsePanels() => [(byte)ControlMessageType.CollapsePanels];
    public static byte[] RotateDevice() => [(byte)ControlMessageType.RotateDevice];
    public static byte[] OpenHardKeyboardSettings() => [(byte)ControlMessageType.OpenHardKeyboardSettings];
    public static byte[] ResetVideo() => [(byte)ControlMessageType.ResetVideo];
    public static byte[] CameraZoomIn() => [(byte)ControlMessageType.CameraZoomIn];
    public static byte[] CameraZoomOut() => [(byte)ControlMessageType.CameraZoomOut];

    public static byte[] GetClipboard(CopyKey copyKey) => [(byte)ControlMessageType.GetClipboard, (byte)copyKey];

    /// <param name="sequence">Echoed back in an ack device message; 0 asks for no ack.</param>
    /// <param name="paste">Also paste the text into the focused field.</param>
    public static byte[] SetClipboard(ulong sequence, string text, bool paste)
    {
        var head = new byte[9];
        BinaryPrimitives.WriteUInt64BigEndian(head, sequence);
        head[8] = paste ? (byte)1 : (byte)0;
        return WithString(ControlMessageType.SetClipboard, head, text, ScrcpyProtocol.ClipboardTextMaxLength);
    }

    /// <summary>Turns the device screen on or off while mirroring continues.</summary>
    public static byte[] SetDisplayPower(bool on) => [(byte)ControlMessageType.SetDisplayPower, on ? (byte)1 : (byte)0];

    public static byte[] CameraSetTorch(bool on) => [(byte)ControlMessageType.CameraSetTorch, on ? (byte)1 : (byte)0];

    /// <summary>Starts an app by package name (or "?name" to search by label, "+name" to force-stop first).</summary>
    public static byte[] StartApp(string name)
    {
        var bytes = TruncateUtf8(name, ScrcpyProtocol.StartAppNameMaxLength);
        var buf = new byte[2 + bytes.Length];
        buf[0] = (byte)ControlMessageType.StartApp;
        buf[1] = (byte)bytes.Length;
        bytes.CopyTo(buf.AsSpan(2));
        return buf;
    }

    public static byte[] ResizeDisplay(ushort width, ushort height)
    {
        var buf = new byte[5];
        buf[0] = (byte)ControlMessageType.ResizeDisplay;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1), width);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(3), height);
        return buf;
    }

    /// <summary>Asks the media scanner to index a pushed file or folder so it shows in the gallery.</summary>
    public static byte[] ScanFile(string devicePath) =>
        WithString(ControlMessageType.ScanFile, [], devicePath, ScrcpyProtocol.ScanFilePathMaxLength);

    /// <summary>UTF-8 bytes of <paramref name="text"/>, cut to at most <paramref name="maxBytes"/> without splitting a character.</summary>
    public static byte[] TruncateUtf8(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) return bytes;
        int len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--; // bytes[len] is the first byte left out
        return bytes[..len];
    }

    /// <summary>scrcpy's sc_float_to_u16fp: [0, 1] to 16-bit fixed point, 1.0 saturating at 0xffff.</summary>
    public static ushort FloatToU16Fixed(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        uint u = (uint)(f * 65536f);
        return u >= 0xffff ? (ushort)0xffff : (ushort)u;
    }

    /// <summary>scrcpy's sc_float_to_i16fp: [-1, 1] to signed 16-bit fixed point, 1.0 saturating at 0x7fff.</summary>
    public static short FloatToI16Fixed(float f)
    {
        f = Math.Clamp(f, -1f, 1f);
        int i = (int)(f * 32768f);
        return i >= 0x7fff ? (short)0x7fff : (short)i;
    }

    private static void WritePosition(Span<byte> buf, DevicePosition p)
    {
        BinaryPrimitives.WriteInt32BigEndian(buf, p.X);
        BinaryPrimitives.WriteInt32BigEndian(buf[4..], p.Y);
        BinaryPrimitives.WriteUInt16BigEndian(buf[8..], p.FrameWidth);
        BinaryPrimitives.WriteUInt16BigEndian(buf[10..], p.FrameHeight);
    }

    /// <summary>type, fixed head, u32 length, UTF-8 text.</summary>
    private static byte[] WithString(ControlMessageType type, ReadOnlySpan<byte> head, string text, int maxBytes)
    {
        var bytes = TruncateUtf8(text, maxBytes);
        var buf = new byte[1 + head.Length + 4 + bytes.Length];
        buf[0] = (byte)type;
        head.CopyTo(buf.AsSpan(1));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1 + head.Length), (uint)bytes.Length);
        bytes.CopyTo(buf.AsSpan(5 + head.Length));
        return buf;
    }
}
