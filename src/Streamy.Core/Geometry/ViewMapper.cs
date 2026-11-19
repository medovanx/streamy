using Streamy.Core.Protocol;

namespace Streamy.Core.Geometry;

/// <summary>
/// Fits the video frame inside the window keeping its aspect ratio (letterboxing), and maps window
/// points back to frame coordinates for touch events.
/// </summary>
public readonly record struct ViewMapper(int ViewWidth, int ViewHeight, int FrameWidth, int FrameHeight)
{
    /// <summary>The rectangle (x, y, width, height) the frame occupies inside the view.</summary>
    public (int X, int Y, int Width, int Height) ContentRect
    {
        get
        {
            if (ViewWidth <= 0 || ViewHeight <= 0 || FrameWidth <= 0 || FrameHeight <= 0) return (0, 0, 0, 0);
            // Compare aspect ratios with integers to avoid rounding flips.
            if ((long)ViewWidth * FrameHeight > (long)ViewHeight * FrameWidth)
            {
                int w = (int)((long)ViewHeight * FrameWidth / FrameHeight);
                return ((ViewWidth - w) / 2, 0, w, ViewHeight);
            }
            int h = (int)((long)ViewWidth * FrameHeight / FrameWidth);
            return (0, (ViewHeight - h) / 2, ViewWidth, h);
        }
    }

    /// <summary>
    /// Maps a view point to the frame. Points in the black bars are clamped to the nearest edge, so a drag that
    /// leaves the picture keeps moving along its border instead of being dropped.
    /// </summary>
    public DevicePosition Map(int viewX, int viewY)
    {
        var (x, y, w, h) = ContentRect;
        if (w == 0 || h == 0) return new DevicePosition(0, 0, (ushort)FrameWidth, (ushort)FrameHeight);
        int fx = (int)Math.Clamp((long)(viewX - x) * FrameWidth / w, 0, FrameWidth - 1);
        int fy = (int)Math.Clamp((long)(viewY - y) * FrameHeight / h, 0, FrameHeight - 1);
        return new DevicePosition(fx, fy, (ushort)FrameWidth, (ushort)FrameHeight);
    }

    public bool Contains(int viewX, int viewY)
    {
        var (x, y, w, h) = ContentRect;
        return viewX >= x && viewX < x + w && viewY >= y && viewY < y + h;
    }
}
