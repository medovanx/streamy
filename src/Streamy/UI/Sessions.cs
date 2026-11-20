namespace Streamy.UI;

/// <summary>The open phone windows (one mirror per phone), shared by the device list and the other windows.</summary>
internal static class Sessions
{
    /// <summary>Serial → the window mirroring that phone's screen. App windows are not in here.</summary>
    public static Dictionary<string, SessionWindow> Mirrors { get; } = [];

    public static bool IsMirroring(string serial) => Mirrors.TryGetValue(serial, out var w) && !w.IsDisposed;
}
