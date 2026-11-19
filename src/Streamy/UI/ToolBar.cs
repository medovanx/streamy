namespace Streamy.UI;

/// <summary>
/// A vertical strip of icon buttons beside the phone's screen. The buttons never take keyboard focus,
/// so typing keeps going to the phone after a click.
/// </summary>
internal sealed class ToolBar : Control
{
    public const int BarWidth = 44;
    private static readonly Color Back = Color.FromArgb(0x20, 0x20, 0x20);

    public ToolBar()
    {
        Width = BarWidth;
        Dock = DockStyle.Right;
        BackColor = Back;
        SetStyle(ControlStyles.Selectable, false);
    }

    private int _nextTop;

    /// <summary>A thin gap between groups of buttons.</summary>
    public void AddSeparator() => _nextTop += BarWidth / 3;

    public ToolButton Add(string glyph, string tip, ToolTip tips, Action onClick, Action? onRightClick = null)
    {
        var b = new ToolButton(glyph) { Top = _nextTop, Width = BarWidth, Height = BarWidth };
        _nextTop += BarWidth;
        b.MouseUp += (_, e) =>
        {
            if (!b.ClientRectangle.Contains(e.Location)) return;
            if (e.Button == MouseButtons.Left) onClick();
            else if (e.Button == MouseButtons.Right) onRightClick?.Invoke();
        };
        tips.SetToolTip(b, tip);
        Controls.Add(b);
        return b;
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
        TextRenderer.DrawText(e.Graphics, Text, IconFont, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
}
