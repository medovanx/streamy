namespace Streamy.Core.Protocol;

/// <summary>
/// Constants of the scrcpy client/server protocol. The server refuses any client whose version string
/// differs from its own, so <see cref="ServerVersion"/> must match the scrcpy-server pinned in deps.json.
/// Values come from the scrcpy 5.0.1 sources (control_msg.h, device_msg.h, demuxer.c, DesktopConnection.java).
/// </summary>
public static class ScrcpyProtocol
{
    public const string ServerVersion = "5.0.1";

    /// <summary>Where the server jar is pushed on the device.</summary>
    public const string DeviceServerPath = "/data/local/tmp/scrcpy-server.jar";

    public const string ServerMainClass = "com.genymobile.scrcpy.Server";

    /// <summary>Abstract socket name prefix; the full name is the prefix plus the SCID as 8 hex digits.</summary>
    public const string SocketNamePrefix = "scrcpy_";

    /// <summary>The device name sent on the first socket, NUL padded.</summary>
    public const int DeviceNameFieldLength = 64;

    public const int PacketHeaderSize = 12;

    public const int ControlMessageMaxSize = 1 << 18;
    public const int InjectTextMaxLength = 300;
    public const int ClipboardTextMaxLength = ControlMessageMaxSize - 14;
    public const int ScanFilePathMaxLength = 256;
    public const int StartAppNameMaxLength = 255;

    public const int DeviceMessageMaxSize = 1 << 18;
    public const int DeviceMessageTextMaxLength = DeviceMessageMaxSize - 5;

    public const ulong PointerIdMouse = ulong.MaxValue;
    public const ulong PointerIdGenericFinger = ulong.MaxValue - 1;
    public const ulong PointerIdVirtualFinger = ulong.MaxValue - 2;

    public static string SocketName(uint scid) => SocketNamePrefix + scid.ToString("x8");
}
