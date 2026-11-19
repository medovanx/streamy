using Streamy.Core.Adb;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// The selected phone's details beside the device list: Android version, chip, GPU, RAM, storage, battery and
/// screen. Read with one adb call when the selection changes, then every 15 seconds while it stays selected.
/// </summary>
internal sealed class DetailsPanel : Panel
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);

    private readonly Label _title = new() { AutoSize = false, Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI Semibold", 12f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
    private readonly TableLayoutPanel _table = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10, 4, 10, 10), AutoScroll = true };
    private readonly Dictionary<string, Label> _values = [];
    private readonly ContextMenuStrip _menu = new();
    private string? _serial;
    private DateTime _loadedAt;
    private bool _loading;
    private DeviceInfo? _info;

    private static readonly string[] Rows = ["Android", "Model", "Build", "CPU", "GPU", "RAM", "Storage", "Battery", "Screen"];

    public DetailsPanel()
    {
        Width = 330;
        Dock = DockStyle.Right;
        Padding = new Padding(1, 0, 0, 0);
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var row in Rows)
        {
            var name = new Label { Text = row, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 6, 0) };
            var value = new Label { AutoSize = true, MaximumSize = new Size(230, 0), Margin = new Padding(0, 6, 0, 0) };
            _values[row] = value;
            _table.Controls.Add(name);
            _table.Controls.Add(value);
        }
        Controls.Add(_table);
        Controls.Add(_title);
        _menu.Items.Add("Copy details", null, (_, _) => CopyDetails());
        ContextMenuStrip = _table.ContextMenuStrip = _menu;
        Show(null, "Select a phone to see its details.");
    }

    /// <summary>Called on every device list refresh with the selected ready phone, or null.</summary>
    public async Task UpdateAsync(string? serial, string displayName)
    {
        if (serial is null)
        {
            _serial = null;
            Show(null, "Select a phone to see its details.");
            return;
        }
        bool changed = serial != _serial;
        if (!changed && (DateTime.Now - _loadedAt < RefreshEvery || _loading)) return;
        if (changed)
        {
            _serial = serial;
            _info = null;
            Show(null, displayName);
            foreach (var v in _values.Values) v.Text = "";
        }
        _loading = true;
        try
        {
            var info = await AdbClient.Default.DeviceInfoAsync(serial);
            if (serial != _serial) return; // the selection moved on meanwhile
            _info = info;
            _loadedAt = DateTime.Now;
            Show(info, info.Name);
        }
        catch (Exception ex)
        {
            Log.Info($"Device info {serial}: {ex.Message}");
            if (serial == _serial) _values["Android"].Text = "Could not read the phone's details.";
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(DeviceInfo? info, string title)
    {
        _title.Text = title;
        _table.Visible = _serial is not null;
        if (info is null) return;
        _values["Android"].Text = info.AndroidText;
        _values["Model"].Text = $"{info.Manufacturer} {info.Model}".Trim();
        _values["Build"].Text = info.Build ?? "Unknown";
        _values["CPU"].Text = info.CpuText.Length > 0 ? info.CpuText : "Unknown";
        _values["GPU"].Text = info.GpuText;
        _values["RAM"].Text = info.RamText;
        _values["Storage"].Text = info.StorageText;
        _values["Battery"].Text = info.BatteryText;
        _values["Screen"].Text = info.ScreenText;
    }

    private void CopyDetails()
    {
        if (_info is null) return;
        var text = _title.Text + Environment.NewLine + string.Join(Environment.NewLine, Rows.Select(r => $"{r}: {_values[r].Text}"));
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawLine(pen, 0, 0, 0, Height); // a hairline between the list and the details
    }
}
