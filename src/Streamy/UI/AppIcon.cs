using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Streamy.UI;

/// <summary>
/// The Streamy mark: a white phone with a play triangle, sending two signal arcs, on a blue-to-cyan rounded
/// square. Drawn in code so every icon size is sharp; <c>--render-icon DIR</c> writes app.ico and a 512 px PNG.
/// </summary>
internal static class AppIcon
{
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    private static Icon? _cached;

    /// <summary>The icon embedded in the exe, for window title bars.</summary>
    public static Icon? Load()
    {
        if (_cached is not null) return _cached;
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        return s is null ? null : _cached = new Icon(s);
    }

    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size;

        // Background: rounded square, diagonal gradient.
        var bg = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
        using (var path = Rounded(bg, s * 0.22f))
        using (var brush = new LinearGradientBrush(bg, Color.FromArgb(0x4F, 0x46, 0xE5), Color.FromArgb(0x06, 0xB6, 0xD4), 45f))
        {
            g.FillPath(brush, path);
            using var shine = new LinearGradientBrush(bg, Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f);
            g.FillPath(shine, path);
        }

        bool tiny = size < 32;
        // Phone, a little left of centre so the signal arcs fit on the right.
        float pw = s * (tiny ? 0.44f : 0.38f), ph = s * (tiny ? 0.66f : 0.60f);
        float px = tiny ? (s - pw) / 2 : s * 0.20f, py = (s - ph) / 2;
        var phone = new RectangleF(px, py, pw, ph);
        float stroke = Math.Max(1.5f, s * 0.055f);
        using (var path = Rounded(phone, pw * 0.22f))
        using (var pen = new Pen(Color.White, stroke) { LineJoin = LineJoin.Round })
        using (var fill = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        // Play triangle in the phone.
        float cx = px + pw / 2 + pw * 0.04f, cy = py + ph / 2, t = pw * 0.24f;
        using (var white = new SolidBrush(Color.White))
        {
            g.FillPolygon(white, [new PointF(cx - t * 0.8f, cy - t), new PointF(cx - t * 0.8f, cy + t), new PointF(cx + t * 0.95f, cy)]);
        }

        if (!tiny)
        {
            // Two arcs: the screen going out.
            using var arc = new Pen(Color.White, stroke * 0.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float ax = px + pw + s * 0.02f;
            for (int i = 1; i <= 2; i++)
            {
                float r = s * (0.09f + 0.10f * i);
                arc.Color = Color.FromArgb(i == 1 ? 255 : 190, 255, 255, 255);
                g.DrawArc(arc, ax - r, cy - r, r * 2, r * 2, -40, 80);
            }
        }
        return bmp;
    }

    /// <summary>Writes app.ico (PNG entries for every size Windows asks for) and icon.png (512 px).</summary>
    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);
        // 256 px as PNG (the format Windows expects there); smaller sizes as 32-bit DIBs, which every icon
        // loader (including older GDI+ paths) can read.
        var images = IcoSizes.Select(sz =>
        {
            using var b = Draw(sz);
            return (Size: sz, Png: sz >= 256 ? ToPng(b) : ToDib(b));
        }).ToList();

        using (var fs = File.Create(Path.Combine(dir, "app.ico")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (sz, png) in images)
            {
                w.Write((byte)(sz >= 256 ? 0 : sz));
                w.Write((byte)(sz >= 256 ? 0 : sz));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(png.Length);
                w.Write(offset);
                offset += png.Length;
            }
            foreach (var (_, png) in images) w.Write(png);
        }
        using var big = Draw(512);
        big.Save(Path.Combine(dir, "icon.png"), ImageFormat.Png);
    }

    private static byte[] ToPng(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>An icon image as BITMAPINFOHEADER + bottom-up BGRA pixels + an all-zero AND mask.</summary>
    private static byte[] ToDib(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        int maskStride = ((w + 31) / 32) * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
        bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                bw.Write(row);
            }
        }
        finally
        {
            b.UnlockBits(data);
        }
        bw.Write(new byte[maskStride * h]);
        return ms.ToArray();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
