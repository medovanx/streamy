using System.Drawing.Drawing2D;
using Streamy.Core.Media;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// Where the gallery's photos and videos were taken, on an OpenStreetMap map: nearby items gather into one pin
/// with the newest photo and a count. Drag to move, wheel to zoom; clicking a pin shows those items in the gallery.
/// </summary>
internal sealed class MapWindow : Form
{
    private readonly MapView _map;

    public MapWindow(string deviceName, IReadOnlyList<(MediaItem Item, GeoPoint Point)> located, ThumbnailLoader thumbs, Action<IReadOnlyList<MediaItem>, string> show)
    {
        Text = $"{deviceName} map - Streamy";
        Icon = AppIcon.Load();
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(500, 360);
        StartPosition = FormStartPosition.CenterScreen;
        _map = new MapView(located, thumbs) { Dock = DockStyle.Fill };
        _map.PinClicked += (items, label) => show(items, label);
        var status = new Label { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0),
            Text = $"{located.Count:N0} photos and videos with a location. Drag to move, scroll to zoom, click a pin to see its photos in the gallery." };
        Controls.Add(_map);
        Controls.Add(status);
    }

    /// <summary>
    /// The map loads tiles from OpenStreetMap: say so once and only go on with the user's agreement. Returns false
    /// when they declined.
    /// </summary>
    public static bool Consent(IWin32Window owner)
    {
        if (AppSettings.Current.MapConsent) return true;
        var answer = MessageBox.Show(owner,
            "The map is drawn with tiles downloaded from OpenStreetMap (openstreetmap.org).\n\n" +
            "OpenStreetMap sees this PC's IP address and which areas of the map are shown, which are the areas where " +
            "your photos were taken. Your photos and their exact coordinates are not sent.\n\n" +
            "Tiles are kept on this PC, so an area is downloaded once. Show the map?",
            "Map", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (answer != DialogResult.OK) return false;
        AppSettings.Current.MapConsent = true;
        AppSettings.Current.Save();
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _map.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>A slippy map with photo pins, drawn here rather than in a browser control.</summary>
internal sealed class MapView : Control
{
    private readonly IReadOnlyList<(MediaItem Item, GeoPoint Point)> _located;
    private readonly ThumbnailLoader? _thumbs;
    private readonly MapTiles _tiles = new();
    private readonly int _tile;
    private double _zoom = 3;
    private double _centerX, _centerY; // world pixels at _zoom
    private Point? _dragFrom;
    private bool _dragged;
    private List<Pin> _pins = [];
    private bool _fitted;
    private bool _repaintQueued;

    public event Action<IReadOnlyList<MediaItem>, string>? PinClicked;

    private sealed record Pin(Rectangle Bounds, List<MediaItem> Items, GeoPoint Center);

    /// <summary>Raised on a worker thread when a map tile arrives (used by the developer switch).</summary>
    public event Action? TileArrived;

    public MapView(IReadOnlyList<(MediaItem Item, GeoPoint Point)> located, ThumbnailLoader? thumbs)
    {
        _located = located;
        _thumbs = thumbs;
        _tile = (int)Math.Round(256 * DeviceDpi / 96.0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(0xAA, 0xD3, 0xDF);
        _tiles.Arrived += QueueRepaint;
        _tiles.Arrived += () => TileArrived?.Invoke();
        if (_thumbs is not null)
        {
            _thumbs.Loaded += QueueRepaint;
            _thumbs.Want(located.Select(l => l.Item).Take(400));
        }
    }

    private void QueueRepaint()
    {
        if (_repaintQueued || IsDisposed || !IsHandleCreated) return;
        _repaintQueued = true;
        BeginInvoke(() => { _repaintQueued = false; Invalidate(); });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Fit();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_fitted) Fit();
    }

    /// <summary>Starts zoomed to show every pin.</summary>
    private void Fit()
    {
        if (Width < 50 || Height < 50) return;
        var points = _located.Select(l => l.Point).ToList();
        _zoom = MapMath.FitZoom(points, Width, Height, tile: _tile);
        if (points.Count > 0)
        {
            var px = points.Select(p => MapMath.ToPixel(p, _zoom, _tile)).ToList();
            _centerX = (px.Min(p => p.X) + px.Max(p => p.X)) / 2;
            _centerY = (px.Min(p => p.Y) + px.Max(p => p.Y)) / 2;
        }
        _fitted = true;
        Invalidate();
    }

    // ---- Drawing ------------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int z = (int)Math.Round(_zoom);
        double left = _centerX - Width / 2.0, top = _centerY - Height / 2.0;
        int x0 = (int)Math.Floor(left / _tile), y0 = (int)Math.Floor(top / _tile);
        int x1 = (int)Math.Floor((left + Width) / _tile), y1 = (int)Math.Floor((top + Height) / _tile);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                var dest = new Rectangle((int)Math.Round(tx * (double)_tile - left), (int)Math.Round(ty * (double)_tile - top), _tile, _tile);
                if (_tiles.Get(z, tx, ty) is { } bmp) g.DrawImage(bmp, dest);
                else if (ParentTile(z, tx, ty, out var src) is { } parent) g.DrawImage(parent, dest, src, GraphicsUnit.Pixel); // a lower zoom's tile, enlarged, until this one arrives
            }
        }
        DrawPins(g, left, top);

        // OpenStreetMap's licence asks for the credit to be visible on the map.
        var credit = MapTiles.Attribution;
        var size = TextRenderer.MeasureText(credit, Font);
        var box = new Rectangle(Width - size.Width - 8, Height - size.Height - 4, size.Width + 8, size.Height + 4);
        using var back = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        g.FillRectangle(back, box);
        TextRenderer.DrawText(g, credit, Font, box, Color.FromArgb(0x33, 0x33, 0x33), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>The nearest lower zoom's tile that is ready, and the part of it covering this tile.</summary>
    private Bitmap? ParentTile(int z, int x, int y, out Rectangle src)
    {
        src = Rectangle.Empty;
        for (int up = 1; up <= 4 && z - up >= 0; up++)
        {
            int n = 1 << z;
            int wx = ((x % n) + n) % n;
            int px = wx >> up, py = y >> up;
            if (_tiles.Get(z - up, px, py) is not { } bmp) continue;
            int part = 256 >> up;
            src = new Rectangle((wx - (px << up)) * part, (y - (py << up)) * part, part, part);
            return bmp;
        }
        return null;
    }

    private void DrawPins(Graphics g, double left, double top)
    {
        // Items whose pins would overlap share one: a grid of cells about one pin wide.
        int cell = (int)Math.Round(64 * DeviceDpi / 96.0);
        var groups = new Dictionary<(long, long), List<(MediaItem Item, GeoPoint Point, double X, double Y)>>();
        double world = _tile * Math.Pow(2, Math.Round(_zoom));
        foreach (var (item, point) in _located)
        {
            var (x, y) = MapMath.ToPixel(point, Math.Round(_zoom), _tile);
            // The copy of the world nearest to the view.
            x += Math.Round((_centerX - x) / world) * world;
            double sx = x - left, sy = y - top;
            if (sx < -cell || sy < -cell || sx > Width + cell || sy > Height + cell) continue;
            var key = ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add((item, point, sx, sy));
        }
        var pins = new List<Pin>();
        int size = (int)Math.Round(52 * DeviceDpi / 96.0);
        using var border = new Pen(Color.White, 3);
        using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        using var badgeBrush = new SolidBrush(Color.FromArgb(0x00, 0x78, 0xD4));
        using var badgeFont = new Font("Segoe UI Semibold", 8.5f);
        var wanted = new List<MediaItem>();
        foreach (var list in groups.Values)
        {
            double cx = list.Average(l => l.X), cy = list.Average(l => l.Y);
            var newest = list.MaxBy(l => l.Item.Taken).Item;
            var r = new Rectangle((int)cx - size / 2, (int)cy - size / 2, size, size);
            var shade = r;
            shade.Offset(2, 3);
            g.FillRectangle(shadow, shade);
            if (_thumbs?.Get(newest) is { } thumb)
            {
                // Square crop of the thumbnail.
                int side = Math.Min(thumb.Width, thumb.Height);
                g.DrawImage(thumb, r, new Rectangle((thumb.Width - side) / 2, (thumb.Height - side) / 2, side, side), GraphicsUnit.Pixel);
            }
            else
            {
                g.FillRectangle(Brushes.DimGray, r);
                wanted.Add(newest);
            }
            g.DrawRectangle(border, r);
            if (list.Count > 1)
            {
                var text = list.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                var ts = TextRenderer.MeasureText(text, badgeFont);
                var badge = new Rectangle(r.Right - ts.Width / 2 - 6, r.Top - 9, ts.Width + 6, ts.Height + 2);
                g.FillRectangle(badgeBrush, badge);
                TextRenderer.DrawText(g, text, badgeFont, badge, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            var center = new GeoPoint(list.Average(l => l.Point.Latitude), list.Average(l => l.Point.Longitude));
            pins.Add(new Pin(Rectangle.Inflate(r, 4, 10), list.OrderByDescending(l => l.Item.Taken).Select(l => l.Item).ToList(), center));
        }
        _pins = pins;
        if (wanted.Count > 0) _thumbs?.Want(wanted);
    }

    // ---- Moving and zooming -------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _dragFrom = e.Location;
        _dragged = false;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragFrom is not { } from)
        {
            Cursor = _pins.Any(p => p.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            return;
        }
        int dx = e.X - from.X, dy = e.Y - from.Y;
        if (!_dragged && Math.Abs(dx) + Math.Abs(dy) < 4) return;
        _dragged = true;
        _centerX -= dx;
        _centerY -= dy;
        _dragFrom = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool click = _dragFrom is not null && !_dragged;
        _dragFrom = null;
        Cursor = Cursors.Default;
        if (!click) return;
        // The pin drawn last is on top.
        if (_pins.LastOrDefault(p => p.Bounds.Contains(e.Location)) is { } pin)
        {
            string place = Geocoder.Default.Name(pin.Center) ?? "this place";
            PinClicked?.Invoke(pin.Items, $"{pin.Items.Count:N0} near {place}");
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!_pins.Any(p => p.Bounds.Contains(e.Location))) ZoomAt(e.Location, 1);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.Location, e.Delta > 0 ? 1 : -1);
    }

    /// <summary>Zooms by whole steps, keeping the point under the mouse where it is.</summary>
    private void ZoomAt(Point p, int step)
    {
        double next = Math.Clamp(Math.Round(_zoom) + step, 1, MapTiles.MaxZoom);
        if (next == _zoom) return;
        double factor = Math.Pow(2, next - _zoom);
        double mx = _centerX - Width / 2.0 + p.X, my = _centerY - Height / 2.0 + p.Y;
        _centerX = mx * factor - p.X + Width / 2.0;
        _centerY = my * factor - p.Y + Height / 2.0;
        _zoom = next;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = 120;
        switch (e.KeyCode)
        {
            case Keys.Left: _centerX -= step; break;
            case Keys.Right: _centerX += step; break;
            case Keys.Up: _centerY -= step; break;
            case Keys.Down: _centerY += step; break;
            case Keys.Add or Keys.Oemplus: ZoomAt(new Point(Width / 2, Height / 2), 1); return;
            case Keys.Subtract or Keys.OemMinus: ZoomAt(new Point(Width / 2, Height / 2), -1); return;
            case Keys.Home: Fit(); return;
            default: return;
        }
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tiles.Arrived -= QueueRepaint;
            if (_thumbs is not null) _thumbs.Loaded -= QueueRepaint;
            _tiles.Dispose();
        }
        base.Dispose(disposing);
    }
}
