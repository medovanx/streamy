using Streamy.Services;

namespace Streamy.UI;

/// <summary>All settings on one page. Video and phone options apply to phones mirrored after saving.</summary>
internal sealed class SettingsWindow : Form
{
    private readonly AppSettings _s = AppSettings.Current;
    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(14, 8, 14, 8) };

    private readonly ComboBox _maxSize = Combo("Phone's own", "2560", "1920", "1600", "1280", "1024", "800");
    private readonly NumericUpDown _bitRate = new() { Minimum = 1, Maximum = 100, Width = 80 };
    private readonly ComboBox _maxFps = Combo("No limit", "120", "90", "60", "30");
    private readonly ComboBox _codec = Combo("H.264 (works everywhere)", "H.265 (sharper at the same bit rate)", "AV1 (newest phones)");
    private readonly CheckBox _audio = Check("Play the phone's sound on the PC (Android 11+)");
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 220 };
    private readonly CheckBox _showTouches = Check("Show touches on the phone while mirroring");
    private readonly CheckBox _stayAwake = Check("Keep the phone awake while plugged in");
    private readonly CheckBox _screenOff = Check("Turn the phone's screen off when mirroring starts");
    private readonly CheckBox _lockOnClose = Check("Lock the phone when its window closes");
    private readonly CheckBox _onTop = Check("Keep phone windows on top");
    private readonly TextBox _shots = new() { Width = 260 };
    private readonly TextBox _recordings = new() { Width = 260 };
    private readonly CheckBox _updates = Check("Check for updates when Streamy starts");

    private static readonly string[] Codecs = ["h264", "h265", "av1"];

    public SettingsWindow()
    {
        Text = "Streamy settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(560, 720);
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Section("Video (new sessions)");
        Row("Resolution", _maxSize);
        Row("Bit rate (Mbit/s)", _bitRate);
        Row("Frame rate", _maxFps);
        Row("Codec", _codec);
        Section("Sound");
        Row("", _audio);
        Row("Volume", _volume);
        Section("Phone (new sessions)");
        Row("", _showTouches);
        Row("", _stayAwake);
        Row("", _screenOff);
        Row("", _lockOnClose);
        Section("Windows and files");
        Row("", _onTop);
        Row("Screenshots", FolderPicker(_shots, SaveFolders.Default(Environment.SpecialFolder.MyPictures)));
        Row("Recordings", FolderPicker(_recordings, SaveFolders.Default(Environment.SpecialFolder.MyVideos)));
        Section("Updates");
        var check = new Button { Text = "Check now", AutoSize = true };
        check.Click += async (_, _) => await UpdateCheck.RunAsync(this, quiet: false);
        Row("", _updates);
        Row($"Version {Updater.CurrentText}", check);

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10) };
        buttons.Controls.AddRange([cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(_grid);
        Controls.Add(buttons);

        LoadValues();
        save.Click += (_, _) => SaveValues();
    }

    private void LoadValues()
    {
        _maxSize.SelectedIndex = Math.Max(0, _maxSize.Items.IndexOf(_s.MaxSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _bitRate.Value = Math.Clamp(_s.BitRateMbps, 1, 100);
        _maxFps.SelectedIndex = Math.Max(0, _maxFps.Items.IndexOf(_s.MaxFps.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _codec.SelectedIndex = Math.Max(0, Array.IndexOf(Codecs, _s.VideoCodec));
        _audio.Checked = _s.Audio;
        _volume.Value = Math.Clamp(_s.Volume, 0, 100);
        _showTouches.Checked = _s.ShowTouches;
        _stayAwake.Checked = _s.StayAwake;
        _screenOff.Checked = _s.TurnScreenOff;
        _lockOnClose.Checked = _s.PowerOffOnClose;
        _onTop.Checked = _s.AlwaysOnTop;
        _shots.Text = _s.ScreenshotFolder ?? "";
        _recordings.Text = _s.RecordingFolder ?? "";
        _updates.Checked = _s.CheckForUpdates;
    }

    private void SaveValues()
    {
        _s.MaxSize = _maxSize.SelectedIndex <= 0 ? 0 : int.Parse((string)_maxSize.SelectedItem!, System.Globalization.CultureInfo.InvariantCulture);
        _s.BitRateMbps = (int)_bitRate.Value;
        _s.MaxFps = _maxFps.SelectedIndex <= 0 ? 0 : int.Parse((string)_maxFps.SelectedItem!, System.Globalization.CultureInfo.InvariantCulture);
        _s.VideoCodec = Codecs[Math.Max(0, _codec.SelectedIndex)];
        _s.Audio = _audio.Checked;
        _s.Volume = _volume.Value;
        _s.ShowTouches = _showTouches.Checked;
        _s.StayAwake = _stayAwake.Checked;
        _s.TurnScreenOff = _screenOff.Checked;
        _s.PowerOffOnClose = _lockOnClose.Checked;
        _s.AlwaysOnTop = _onTop.Checked;
        _s.ScreenshotFolder = string.IsNullOrWhiteSpace(_shots.Text) ? null : _shots.Text.Trim();
        _s.RecordingFolder = string.IsNullOrWhiteSpace(_recordings.Text) ? null : _recordings.Text.Trim();
        _s.CheckForUpdates = _updates.Checked;
        _s.Save();
    }

    private void Section(string title)
    {
        var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
        _grid.Controls.Add(l);
        _grid.SetColumnSpan(l, 2);
    }

    private void Row(string label, Control control)
    {
        _grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) });
        control.Anchor = AnchorStyles.Left;
        _grid.Controls.Add(control);
    }

    private static Control FolderPicker(TextBox box, string placeholder)
    {
        box.PlaceholderText = SaveFolders.Short(placeholder); // "Pictures\Streamy" when left empty
        var browse = new Button { Text = "Browse", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = box.Text.Length > 0 ? box.Text : placeholder };
            if (dlg.ShowDialog() == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange([box, browse]);
        return panel;
    }

    private static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        c.Items.AddRange(items);
        return c;
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };
}

/// <summary>The update check and install flow shared by startup and the Settings button.</summary>
internal static class UpdateCheck
{
    /// <param name="quiet">At startup: say nothing unless there is an update.</param>
    public static async Task RunAsync(IWin32Window owner, bool quiet)
    {
        Updater.Release? rel;
        try
        {
            rel = await Updater.CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Info("Update check failed: " + ex.Message);
            if (!quiet) MessageBox.Show(owner, "Could not reach GitHub to check for updates.", "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (rel is null)
        {
            if (!quiet) MessageBox.Show(owner, $"Streamy {Updater.CurrentText} is the latest version.", "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = MessageBox.Show(owner, $"Streamy {rel.Version.ToString(3)} is available (you have {Updater.CurrentText}). Update now?\n\nStreamy restarts; open phone windows close.",
            "Streamy update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer != DialogResult.Yes) return;
        if (await Updater.InstallAsync(rel)) Application.Exit();
        else MessageBox.Show(owner, "The update could not be installed. You can download it from GitHub.", "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
