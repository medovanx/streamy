using System.Drawing.Drawing2D;
using Streamy.Core.Media;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// A scrolling grid of square thumbnails that only draws (and asks for) what is visible, so albums with thousands
/// of photos scroll smoothly. Click to select, Ctrl to add, Shift for a range, double-click or Enter to open.
/// Videos show a play mark and their length.
/// </summary>
internal sealed class GalleryGrid : Panel
{
    private const int Gap = 6;
    private static readonly Color Back = Color.FromArgb(0x1C, 0x1C, 0x1C);
    private static readonly Color Placeholder = Color.FromArgb(0x2C, 0x2C, 0x2C);
    private static readonly Color Accent = Color.FromArgb(0x00, 0x78, 0xD4);

    private IReadOnlyList<MediaItem> _items = [];
    private readonly HashSet<long> _selected = [];
    private int _anchor = -1, _focus = -1;
    private ThumbnailLoader? _loader;

    public event Action? SelectionChanged;
    public event Action<MediaItem>? ItemActivated;
    public event Action? DeleteRequested;

    public int TileSize { get; }

    public GalleryGrid()
    {
        TileSize = (int)Math.Round(150 * DeviceDpi / 96.0);
        AutoScroll = true;
        DoubleBuffered = true;
        BackColor = Back;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
    }

    public ThumbnailLoader? Loader
    {
        get => _loader;
        set { _loader = value; RequestVisible(); Invalidate(); }
    }

    public IReadOnlyList<MediaItem> Items
    {
        get => _items;
        set
        {
            _items = value;
            _selected.Clear();
            _anchor = _focus = -1;
            AutoScrollPosition = Point.Empty;
            UpdateScrollSize();
            RequestVisible();
            Invalidate();
            SelectionChanged?.Invoke();
        }
    }

    public IReadOnlyList<MediaItem> Selected => _items.Where(i => _selected.Contains(i.Id)).ToList();

    public void SelectAll()
    {
        foreach (var i in _items) _selected.Add(i.Id);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    // ---- Layout -------------------------------------------------------------------------------------------

    private int Columns => Math.Max(1, (ClientSize.Width - Gap) / (TileSize + Gap));
    private int Rows => (_items.Count + Columns - 1) / Columns;

    private void UpdateScrollSize() => AutoScrollMinSize = new Size(0, Gap + Rows * (TileSize + Gap));

    private Rectangle TileRect(int index)
    {
        int col = index % Columns, row = index / Columns;
        return new Rectangle(Gap + col * (TileSize + Gap), Gap + row * (TileSize + Gap) + AutoScrollPosition.Y, TileSize, TileSize);
    }

    private int HitTest(Point p)
    {
        int y = p.Y - AutoScrollPosition.Y - Gap, x = p.X - Gap;
        if (x < 0 || y < 0) return -1;
        int col = x / (TileSize + Gap), row = y / (TileSize + Gap);
        if (col >= Columns || x % (TileSize + Gap) >= TileSize || y % (TileSize + Gap) >= TileSize) return -1;
        int index = row * Columns + col;
        return index < _items.Count ? index : -1;
    }

    private (int First, int Last) VisibleRange()
    {
        int top = -AutoScrollPosition.Y;
        int firstRow = Math.Max(0, (top - Gap) / (TileSize + Gap));
        int lastRow = (top + ClientSize.Height) / (TileSize + Gap);
        return (firstRow * Columns, Math.Min(_items.Count - 1, (lastRow + 1) * Columns - 1));
    }

    /// <summary>Asks for the visible thumbnails first, then two screens below and one above.</summary>
    private void RequestVisible()
    {
        if (_loader is null || _items.Count == 0) return;
        var (first, last) = VisibleRange();
        int page = Math.Max(Columns, last - first + 1);
        var wanted = new List<MediaItem>();
        for (int i = first; i <= last; i++) wanted.Add(_items[i]);
        for (int i = last + 1; i <= Math.Min(_items.Count - 1, last + 2 * page); i++) wanted.Add(_items[i]);
        for (int i = first - 1; i >= Math.Max(0, first - page); i--) wanted.Add(_items[i]);
        _loader.Want(wanted);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollSize();
        RequestVisible();
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        RequestVisible();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        RequestVisible();
        Invalidate();
    }

    // ---- Drawing ------------------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Back);
        if (_items.Count == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (first, last) = VisibleRange();
        using var placeholder = new SolidBrush(Placeholder);
        using var badge = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var smallFont = new Font("Segoe UI Semibold", 8.5f);
        for (int i = first; i <= last; i++)
        {
            var item = _items[i];
            var r = TileRect(i);
            if (!r.IntersectsWith(e.ClipRectangle)) continue;

            if (_loader?.Get(item) is { } thumb) g.DrawImage(thumb, r);
            else
            {
                g.FillRectangle(placeholder, r);
                string mark = _loader?.Failed(item) == true ? "?" : item.IsVideo ? "Video" : "";
                if (mark.Length > 0) TextRenderer.DrawText(g, mark, smallFont, r, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            if (item.IsVideo)
            {
                // Play mark in the middle, length at the bottom right.
                int d = TileSize / 4;
                var c = new Rectangle(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
                g.FillEllipse(badge, c);
                float t = d * 0.22f;
                g.FillPolygon(Brushes.White, [new PointF(c.X + d * 0.42f, c.Y + d / 2f - t), new PointF(c.X + d * 0.42f, c.Y + d / 2f + t), new PointF(c.X + d * 0.68f, c.Y + d / 2f)]);
                var text = item.DurationText;
                var size = TextRenderer.MeasureText(text, smallFont);
                var pill = new Rectangle(r.Right - size.Width - 8, r.Bottom - size.Height - 6, size.Width + 4, size.Height + 2);
                g.FillRectangle(badge, pill);
                TextRenderer.DrawText(g, text, smallFont, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            if (_selected.Contains(item.Id))
            {
                using var pen = new Pen(Accent, 4);
                g.DrawRectangle(pen, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                var tick = new Rectangle(r.X + 8, r.Y + 8, 22, 22);
                using var accent = new SolidBrush(Accent);
                g.FillEllipse(accent, tick);
                using var white = new Pen(Color.White, 2.2f);
                g.DrawLines(white, [new PointF(tick.X + 6, tick.Y + 11.5f), new PointF(tick.X + 9.5f, tick.Y + 15), new PointF(tick.X + 16, tick.Y + 7.5f)]);
            }
            if (i == _focus && Focused)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(r, 2, 2));
        }
    }

    /// <summary>Called when thumbnails arrive; repaints at most every 100 ms.</summary>
    public void ThumbnailsArrived()
    {
        if (_repaintPending || IsDisposed) return;
        _repaintPending = true;
        BeginInvoke(async () =>
        {
            await Task.Delay(100);
            _repaintPending = false;
            if (!IsDisposed) Invalidate();
        });
    }

    private bool _repaintPending;

    // ---- Selection ----------------------------------------------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (i >= 0 && !_selected.Contains(_items[i].Id)) SelectOnly(i);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (i < 0)
        {
            if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0) { _selected.Clear(); Changed(); }
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0) SelectRange(_anchor, i, add: (ModifierKeys & Keys.Control) != 0);
        else if ((ModifierKeys & Keys.Control) != 0)
        {
            if (!_selected.Remove(_items[i].Id)) _selected.Add(_items[i].Id);
            _anchor = _focus = i;
            Changed();
        }
        else SelectOnly(i);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = HitTest(e.Location);
        if (i >= 0 && e.Button == MouseButtons.Left) ItemActivated?.Invoke(_items[i]);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        int move = e.KeyCode switch
        {
            Keys.Left => -1,
            Keys.Right => 1,
            Keys.Up => -Columns,
            Keys.Down => Columns,
            Keys.Home => -_items.Count,
            Keys.End => _items.Count,
            _ => 0,
        };
        if (move != 0)
        {
            int target = Math.Clamp((_focus < 0 ? 0 : _focus) + move, 0, _items.Count - 1);
            if (e.Shift && _anchor >= 0) SelectRange(_anchor, target, add: false);
            else SelectOnly(target);
            ScrollIntoView(target);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.A && e.Control) { SelectAll(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && _focus >= 0) { ItemActivated?.Invoke(_items[_focus]); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete) { DeleteRequested?.Invoke(); e.Handled = true; }
    }

    private void SelectOnly(int i)
    {
        _selected.Clear();
        _selected.Add(_items[i].Id);
        _anchor = _focus = i;
        Changed();
    }

    private void SelectRange(int from, int to, bool add)
    {
        if (!add) _selected.Clear();
        for (int k = Math.Min(from, to); k <= Math.Max(from, to); k++) _selected.Add(_items[k].Id);
        _focus = to;
        Changed();
    }

    private void ScrollIntoView(int i)
    {
        var r = TileRect(i);
        int y = -AutoScrollPosition.Y;
        if (r.Top < 0) AutoScrollPosition = new Point(0, y + r.Top - Gap);
        else if (r.Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, y + r.Bottom - ClientSize.Height + Gap);
        RequestVisible();
        Invalidate();
    }

    private void Changed()
    {
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
