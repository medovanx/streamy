namespace Streamy.UI;

/// <summary>A small text input dialog.</summary>
internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string message, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
        };
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        var label = new Label { Text = message, AutoSize = true, MaximumSize = new Size(420, 0) };
        var box = new TextBox { Width = 320, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        Glyph.Apply(ok, Glyph.Check);
        Glyph.Apply(cancel, Glyph.Cancel);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange([ok, cancel]);
        layout.Controls.AddRange([label, box, buttons]);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
