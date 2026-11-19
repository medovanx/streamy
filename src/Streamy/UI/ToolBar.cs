namespace Streamy.UI;

/// <summary>
/// A vertical strip of icon buttons beside the phone's screen. The buttons never take keyboard focus, so typing
/// keeps going to the phone after a click. When the window is too short for every button at full size (a
/// landscape phone), the buttons shrink to fit instead of being cut off.
/// </summary>
internal sealed class ToolBar : Control
{
    public const int BarWidth = 44;
    private const int MinButton = 26;
    private static readonly Color Back = Color.FromArgb(0x20, 0x20, 0x20);

    private readonly List<Control?> _items = []; // null = separator

    public ToolBar()
    {
        Width = BarWidth;
        Dock = DockStyle.Right;
        BackColor = Back;
        SetStyle(ControlStyles.Selectable, false);
    }

    /// <summary>A thin gap between groups of buttons.</summary>
    public void AddSeparator()
    {
        _items.Add(null);
        Relayout();
    }

    public ToolButton Add(string glyph, string tip, ToolTip tips, Action onClick, Action? onRightClick = null)
    {
        var b = new ToolButton(glyph) { Width = BarWidth, Height = BarWidth };
        b.MouseUp += (_, e) =>
        {
            if (!b.ClientRectangle.Contains(e.Location)) return;
            if (e.Button == MouseButtons.Left) onClick();
            else if (e.Button == MouseButtons.Right) onRightClick?.Invoke();
        };
        tips.SetToolTip(b, tip);
        Controls.Add(b);
        _items.Add(b);
        Relayout();
        return b;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    private void Relayout()
    {
        int buttons = _items.Count(i => i is not null), gaps = _items.Count - buttons;
        if (buttons == 0) return;
        // Full size when it fits; otherwise shrink buttons (and gaps with them) to the available height.
        int size = Math.Clamp((Height - gaps * BarWidth / 3) / buttons, MinButton, BarWidth);
        if (Height <= 0) size = BarWidth;
        int gap = size / 3, top = 0;
        foreach (var item in _items)
        {
            if (item is null) { top += gap; continue; }
            item.Bounds = new Rectangle(0, top, BarWidth, size);
            top += size;
        }
    }
}

/// <summary>An icon drawn with Segoe Fluent Icons (Windows 11) or Segoe MDL2 Assets (Windows 10).</summary>
internal sealed class ToolButton : Control
{
    private static readonly Font IconFont = CreateIconFont();
    private bool _hover, _pressed;

    public ToolButton(string glyph)
    {
        Text = glyph;
        ForeColor = Color.FromArgb(0xE6, 0xE6, 0xE6);
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    private static Font CreateIconFont()
    {
        using var probe = new Font("Segoe Fluent Icons", 12f);
        return probe.Name == "Segoe Fluent Icons" ? new Font("Segoe Fluent Icons", 12f) : new Font("Segoe MDL2 Assets", 12f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bg = _pressed ? Color.FromArgb(0x40, 0x40, 0x40) : _hover ? Color.FromArgb(0x33, 0x33, 0x33) : Parent?.BackColor ?? Color.Black;
        e.Graphics.Clear(bg);
        // Grayscale antialiasing: ClearType's coloured fringes look wrong on the dark bar.
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(ForeColor);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(Text, IconFont, brush, ClientRectangle, format);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
}
