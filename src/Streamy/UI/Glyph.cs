namespace Streamy.UI;

/// <summary>
/// Icon code points from Segoe Fluent Icons (Windows 11), also present in Segoe MDL2 Assets (Windows 10).
/// Written as numbers so editors and tools never turn them into invisible private-use characters.
/// </summary>
internal static class Glyph
{
    public static readonly string Power = G(0xE7E8);
    public static readonly string VolumeUp = G(0xE995);
    public static readonly string VolumeDown = G(0xE993);
    public static readonly string Rotate = G(0xE7AD);
    public static readonly string Back = G(0xE72B);
    public static readonly string Home = G(0xE80F);
    public static readonly string Recents = G(0xE7C4);
    public static readonly string Notifications = G(0xEA8F);
    public static readonly string Folder = G(0xE8B7);
    public static readonly string Camera = G(0xE722);
    public static readonly string Record = G(0xE7C8);
    public static readonly string Speaker = G(0xE767);
    public static readonly string Muted = G(0xE74F);
    public static readonly string Settings = G(0xE713);
    public static readonly string Fullscreen = G(0xE740);
    public static readonly string Pin = G(0xE718);

    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);
}
