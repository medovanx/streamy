using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Streamy.Video;

/// <summary>Converts a YUV frame to a 32-bit bitmap on the CPU, for snapshots (not for live display).</summary>
public static class FrameSnapshot
{
    public static Bitmap ToBitmap(VideoFrame f)
    {
        var bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, f.Width, f.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[f.Width * 4];
            var (kr, kb) = f.Color.Bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
            double kg = 1 - kr - kb;
            for (int y = 0; y < f.Height; y++)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    double Y = f.Y[y * f.Width + x];
                    int ci = (y / 2) * f.ChromaWidth + x / 2;
                    double cb = f.U[ci] - 128, cr = f.V[ci] - 128;
                    if (!f.Color.FullRange)
                    {
                        Y = (Y - 16) * 255 / 219;
                        cb *= 255.0 / 224;
                        cr *= 255.0 / 224;
                    }
                    double r = Y + 2 * (1 - kr) * cr;
                    double b = Y + 2 * (1 - kb) * cb;
                    double g = (Y - kr * r - kb * b) / kg;
                    row[x * 4] = Clamp(b);
                    row[x * 4 + 1] = Clamp(g);
                    row[x * 4 + 2] = Clamp(r);
                    row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
