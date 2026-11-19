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
    public static readonly string Up = G(0xE74A);
    public static readonly string Refresh = G(0xE72C);
    public static readonly string Forward = G(0xE72A);
    public static readonly string Upload = G(0xE898);
    public static readonly string Download = G(0xE896);
    public static readonly string NewFolder = G(0xE8F4);
    public static readonly string Rename = G(0xE8AC);
    public static readonly string Delete = G(0xE74D);
    public static readonly string Places = G(0xE728);

    private static string G(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static readonly string FontName = IconFontName();

    private static string IconFontName()
    {
        using var probe = new Font("Segoe Fluent Icons", 10f);
        return probe.Name == "Segoe Fluent Icons" ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    /// <summary>Draws a glyph as a small transparent image for buttons and menus.</summary>
    public static Bitmap Image(string glyph, Color color, int size = 16)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(FontName, size * 0.72f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size + 1), format);
        return bmp;
    }

    /// <summary>
    /// Puts a glyph before a button's text, sized to the screen's DPI. Icon and text are centred together as one
    /// group, so a wide button keeps them side by side in its middle.
    /// </summary>
    public static void Apply(ButtonBase button, string glyph)
    {
        int size = (int)Math.Round(16 * button.DeviceDpi / 96.0);
        button.Image = Image(glyph, button.ForeColor, size);
        button.ImageAlign = ContentAlignment.MiddleCenter;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.Padding = new Padding(4, 0, 4, 0);
    }

    public static readonly string Mirror = G(0xE7F4);
    public static readonly string MirrorAll = G(0xE8A9);
    public static readonly string QrCode = G(0xED14);
    public static readonly string Key = G(0xE8D7);
    public static readonly string Link = G(0xE71B);
    public static readonly string Wifi = G(0xE701);
    public static readonly string FolderOpen = G(0xE838);
    public static readonly string Save = G(0xE74E);
    public static readonly string Cancel = G(0xE711);
    public static readonly string Check = G(0xE73E);
}
