using System.Globalization;
using Streamy.Core.Apps;
using Streamy.Core.Files;
using Streamy.Services;
using Streamy.Session;

namespace Streamy.UI;

/// <summary>
/// The phone's apps with their icons: open one on the phone or in its own window on the PC (a virtual display, so
/// the phone's screen stays free), and manage them: force stop, clear data, uninstall, back up the APK.
/// </summary>
internal sealed class AppsWindow : Form
{
    private readonly string _serial;
    private readonly string _deviceName;
    private readonly ImageList _images = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly TextBox _search = new() { Width = 260, Margin = new Padding(3, 5, 3, 3), PlaceholderText = "Search apps" };
    private readonly ComboBox _show = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly List<Button> _actions = [];
    private readonly ToolTip _tips = new();
    private Button _open = null!, _window = null!, _stop = null!, _clear = null!, _uninstall = null!, _backup = null!;
    private IReadOnlyList<PhoneApp> _apps = [];
    private bool _busy;
    private int _sortColumn;
    private bool _sortDescending;

    public AppsWindow(string serial, string deviceName)
    {
        _serial = serial;
        _deviceName = deviceName;
        Text = $"{deviceName} apps - Streamy";
        Icon = AppIcon.Load();
        ClientSize = new Size(940, 600);
        MinimumSize = new Size(700, 380);
        StartPosition = FormStartPosition.CenterScreen;

        int icon = (int)Math.Round(32 * DeviceDpi / 96.0);
        _images.ImageSize = new Size(icon, icon);
        _list.SmallImageList = _images;
        _list.Columns.Add("App", 300);
        _list.Columns.Add("Version", 130);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Updated", 120);
        _list.Columns.Add("Package", 260);
        _list.ItemActivate += async (_, _) => await LaunchAsync();
        _list.SelectedIndexChanged += (_, _) => UpdateState();
        _list.ColumnClick += (_, e) =>
        {
            _sortDescending = e.Column == _sortColumn && !_sortDescending;
            _sortColumn = e.Column;
            Fill();
        };
        _list.ContextMenuStrip = BuildMenu();

        _show.Items.AddRange(["Apps on the home screen", "Installed apps", "All apps, system too"]);
        _show.SelectedIndex = 0;
        _show.SelectedIndexChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
        top.Controls.Add(_search);
        top.Controls.Add(_show);
        Action("Refresh", async () => await LoadAsync(), top, Glyph.Refresh, "List the apps again");

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 0, 6, 6), WrapContents = true };
        _open = Action("Open", async () => await LaunchAsync(), bar, Glyph.Play, "Open the app on the phone (double-click does the same)");
        _window = Action("Open in window", async () => await OpenInWindowAsync(), bar, Glyph.NewWindow,
            "Open the app in its own window on this PC, on a separate display: the phone's own screen stays free");
        _stop = Action("Force stop", async () => await RunAsync(PhoneApps.ForceStopCommand, "Stopped"), bar, Glyph.Stop, "Close the app and everything it runs");
        _clear = Action("Clear data", async () => await ClearDataAsync(), bar, Glyph.Erase, "Delete the app's data and cache, like a fresh install");
        _uninstall = Action("Uninstall", async () => await UninstallAsync(), bar, Glyph.Delete, "Remove the app from the phone");
        _backup = Action("Back up APK", async () => await BackupAsync(), bar, Glyph.Save, "Save the app's installer (APK) to this PC");

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(top);
        Controls.Add(_status);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F && e.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { _ = LoadAsync(); e.Handled = true; }
        };
        Shown += async (_, _) => await LoadAsync();
    }

    private PhoneApp? Selected => _list.SelectedItems.Count == 1 ? (PhoneApp)_list.SelectedItems[0].Tag! : null;

    // ---- Loading ------------------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        SetBusy(true, "Reading the phone's apps");
        try
        {
            _apps = await PhoneHelper.AppsAsync(_serial);
            Fill();
            _status.Text = Summary();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "apps");
            _status.Text = "Could not list the apps: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
        await Task.WhenAll(LoadIconsAsync(), LoadSizesAsync());
    }

    /// <summary>Sizes take the phone several seconds; the list shows first and they fill in.</summary>
    private async Task LoadSizesAsync()
    {
        if (_apps.Count == 0 || _apps.All(a => a.AppBytes >= 0)) return;
        try
        {
            var withSizes = await PhoneHelper.AppsAsync(_serial, sizes: true);
            if (IsDisposed || _busy) return;
            _apps = withSizes;
            Fill();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Info("app sizes: " + ex.Message);
        }
    }

    private string Summary()
    {
        int user = _apps.Count(a => !a.IsSystem);
        return $"{_apps.Count:N0} apps: {user:N0} installed, {_apps.Count - user:N0} system. Shown: {_list.Items.Count:N0}.";
    }

    /// <summary>Icons on disk show at once; missing ones come from the phone in the background.</summary>
    private async Task LoadIconsAsync()
    {
        var wanted = Listed(_apps).ToList();
        foreach (var a in wanted) ShowAppIcon(a);
        try
        {
            await PhoneHelper.FetchIconsAsync(_serial, wanted, app => BeginInvoke(() => ShowAppIcon(app)));
            var rest = _apps.Except(wanted).ToList();
            if (!IsDisposed) await PhoneHelper.FetchIconsAsync(_serial, rest, app => BeginInvoke(() => ShowAppIcon(app)));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            Log.Info("icons: " + ex.Message);
        }
    }

    private void ShowAppIcon(PhoneApp app)
    {
        if (IsDisposed || _images.Images.ContainsKey(app.IconKey)) return;
        var file = PhoneHelper.IconFile(app);
        if (!File.Exists(file)) return;
        try
        {
            using var src = Image.FromFile(file);
            _images.Images.Add(app.IconKey, new Bitmap(src, _images.ImageSize));
        }
        catch (Exception ex) when (ex is OutOfMemoryException or IOException) { return; }
        foreach (ListViewItem item in _list.Items)
            if (item.Tag is PhoneApp a && a.IconKey == app.IconKey) item.ImageKey = app.IconKey;
    }

    private IEnumerable<PhoneApp> Listed(IEnumerable<PhoneApp> apps)
    {
        var q = _search.Text.Trim();
        return apps.Where(a => _show.SelectedIndex switch
            {
                0 => a.IsLaunchable,
                1 => !a.IsSystem,
                _ => true,
            })
            .Where(a => q.Length == 0 || a.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Fill()
    {
        var keep = Selected?.Package;
        IEnumerable<PhoneApp> rows = Listed(_apps);
        rows = _sortColumn switch
        {
            1 => rows.OrderBy(a => a.VersionName, StringComparer.OrdinalIgnoreCase),
            2 => rows.OrderBy(a => a.TotalBytes),
            3 => rows.OrderBy(a => a.Updated),
            4 => rows.OrderBy(a => a.Package, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase),
        };
        if (_sortDescending) rows = rows.Reverse();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in rows)
        {
            var item = new ListViewItem(a.Label + (a.IsDisabled ? " (disabled)" : "")) { Tag = a, ImageKey = a.IconKey };
            item.SubItems.Add(a.VersionName);
            item.SubItems.Add(a.TotalBytes > 0 ? PhoneFiles.FormatSize(a.TotalBytes) : "");
            item.SubItems.Add(a.Updated.Year >= 2009 ? a.Updated.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "");
            item.SubItems.Add(a.Package);
            if (a.IsDisabled) item.ForeColor = SystemColors.GrayText;
            _list.Items.Add(item);
            if (a.Package == keep) item.Selected = true;
        }
        _list.EndUpdate();
        if (!_busy && _apps.Count > 0) _status.Text = Summary();
        UpdateState();
    }

    // ---- Actions ------------------------------------------------------------------------------------------

    private async Task LaunchAsync()
    {
        if (Selected is not { } app) return;
        if (!app.IsLaunchable) { _status.Text = $"{app.Label} has no screen to open."; return; }
        await RunAsync(PhoneApps.LaunchCommand, "Opened on the phone:");
    }

    private async Task OpenInWindowAsync()
    {
        if (Selected is not { IsLaunchable: true } app) return;
        SetBusy(true, $"Opening {app.Label} in a window");
        try
        {
            await AppWindows.OpenAsync(_serial, _deviceName, app);
            _status.Text = $"{app.Label} is open in its own window. Closing the window closes the app.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "app window");
            _status.Text = $"Could not open {app.Label} in a window: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> RunAsync(Func<string, string> command, string done)
    {
        if (Selected is not { } app) return false;
        SetBusy(true);
        try
        {
            var r = await AdbClient.Default.RunAsync(["shell", command(app.Package)], _serial, TimeSpan.FromMinutes(2));
            var text = r.All;
            bool ok = r.Ok && !text.Contains("Failure", StringComparison.Ordinal) && !text.Contains("Error", StringComparison.Ordinal);
            _status.Text = ok ? $"{done} {app.Label}." : $"{app.Label}: {text}";
            return ok;
        }
        catch (Exception ex)
        {
            _status.Text = $"{app.Label}: {ex.Message}";
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ClearDataAsync()
    {
        if (Selected is not { } app) return;
        if (MessageBox.Show(this, $"Delete all of {app.Label}'s data on the phone? Accounts, settings and downloads in the app are lost; it starts like a fresh install.",
                "Clear data", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        if (await RunAsync(PhoneApps.ClearDataCommand, "Cleared the data of")) await LoadAsync();
    }

    private async Task UninstallAsync()
    {
        if (Selected is not { } app) return;
        if (app.IsSystem)
        {
            _status.Text = $"{app.Label} came with the phone and cannot be uninstalled.";
            return;
        }
        if (MessageBox.Show(this, $"Uninstall {app.Label} from {_deviceName}? Its data is deleted too.",
                "Uninstall", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        if (await RunAsync(PhoneApps.UninstallCommand, "Uninstalled")) await LoadAsync();
    }

    /// <summary>
    /// Copies the app's APK to a PC folder. Apps split into several APKs (most from Google Play) are saved as a
    /// folder with all of them, which "adb install-multiple" or a split-APK installer takes back.
    /// </summary>
    private async Task BackupAsync()
    {
        if (Selected is not { } app) return;
        using var dlg = new FolderBrowserDialog { Description = $"Save {app.Label}'s APK to", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true, $"Backing up {app.Label}");
        try
        {
            var (result, count) = await PhoneHelper.BackupApkAsync(_serial, app, dlg.SelectedPath);
            _status.Text = count == 1 ? $"Saved {result}" : $"Saved {count} APKs (the app is split) in {result}";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not back up {app.Label}: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---- Plumbing -----------------------------------------------------------------------------------------

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        if (text is not null) _status.Text = text;
        UseWaitCursor = busy;
        UpdateState();
    }

    private void UpdateState()
    {
        var app = Selected;
        foreach (var b in _actions) b.Enabled = !_busy;
        _open.Enabled = _window.Enabled = !_busy && app is { IsLaunchable: true };
        _stop.Enabled = _clear.Enabled = _backup.Enabled = !_busy && app is not null;
        _uninstall.Enabled = !_busy && app is { IsSystem: false };
    }

    private Button Action(string text, Func<Task> onClick, Control parent, string glyph, string tip)
    {
        var b = new Button { Text = text, AutoSize = true };
        Glyph.Apply(b, glyph);
        b.Click += async (_, _) => await onClick();
        _tips.SetToolTip(b, tip);
        parent.Controls.Add(b);
        _actions.Add(b);
        return b;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Open", null, async (_, _) => await LaunchAsync());
        var window = menu.Items.Add("Open in window", null, async (_, _) => await OpenInWindowAsync());
        menu.Items.Add(new ToolStripSeparator());
        var stop = menu.Items.Add("Force stop", null, async (_, _) => await RunAsync(PhoneApps.ForceStopCommand, "Stopped"));
        var clear = menu.Items.Add("Clear data", null, async (_, _) => await ClearDataAsync());
        var uninstall = menu.Items.Add("Uninstall", null, async (_, _) => await UninstallAsync());
        var backup = menu.Items.Add("Back up APK", null, async (_, _) => await BackupAsync());
        menu.Items.Add(new ToolStripSeparator());
        var copy = menu.Items.Add("Copy package name", null, (_, _) => { if (Selected is { } a) Clipboard.SetText(a.Package); });
        menu.Opening += (_, e) =>
        {
            var app = Selected;
            if (app is null) { e.Cancel = true; return; }
            open.Enabled = window.Enabled = !_busy && app.IsLaunchable;
            stop.Enabled = clear.Enabled = backup.Enabled = !_busy;
            uninstall.Enabled = !_busy && !app.IsSystem;
        };
        return menu;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tips.Dispose();
        _images.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>An app in its own window: a session on a new virtual display that starts the app.</summary>
internal static class AppWindows
{
    public static async Task OpenAsync(string serial, string deviceName, PhoneApp app)
    {
        var options = AppSettings.Current.ToServerOptions() with
        {
            NewDisplay = "",
            StartApp = app.Package,
            // The phone has one sound output; an app window would double whatever the main window plays.
            Audio = AppSettings.Current.Audio && !Sessions.IsMirroring(serial),
            // Closing an app window must not lock the phone, and keeping it awake is the main window's business.
            PowerOffOnClose = false,
        };
        var session = new MirrorSession(AdbClient.Default, serial, options);
        try
        {
            await session.StartAsync();
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        new SessionWindow(session, $"{app.Label} on {deviceName}", isAppWindow: true).Show();
    }
}
