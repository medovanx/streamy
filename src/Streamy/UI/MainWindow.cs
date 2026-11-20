using Streamy.Core.Adb;
using Streamy.Services;
using Streamy.Session;

namespace Streamy.UI;

/// <summary>
/// The device list: USB and Wi-Fi phones adb can see, plus saved Wi-Fi phones that are not connected right now.
/// Mirror one or all of them, pair new ones (QR code or six-digit code), connect by address, or move a USB phone
/// to Wi-Fi in one click.
/// </summary>
internal sealed class MainWindow : Form
{
    private const int SavedReconnectEvery = 5; // polls (10 s)

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly Button _mirror = new() { Text = "Mirror", AutoSize = true, Enabled = false };
    private readonly Button _mirrorAll = new() { Text = "Mirror all", AutoSize = true, Enabled = false };
    private readonly Button _wifi = new() { Text = "Switch to Wi-Fi", AutoSize = true, Enabled = false };
    private readonly Button _files = new() { Text = "Files", AutoSize = true, Enabled = false };
    private readonly Button _gallery = new() { Text = "Gallery", AutoSize = true, Enabled = false };
    private readonly Button _apps = new() { Text = "Apps", AutoSize = true, Enabled = false };
    private readonly ToolTip _tips = new();
    private readonly DetailsPanel _details = new();
    private readonly NotifyIcon _tray = new() { Text = "Streamy" };
    private readonly Dictionary<string, NotificationWatcher> _watchers = [];
    private string? _balloonSerial;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private readonly Dictionary<string, SessionWindow> _open = Sessions.Mirrors;
    private readonly HashSet<string> _reconnecting = [];
    private readonly HashSet<string> _reconnectLogged = [];
    private IReadOnlyList<AdbDevice> _devices = [];
    private bool _refreshing;
    private bool _rebuilding;
    private string? _signature;
    private int _pollCount;

    public MainWindow()
    {
        Text = $"Streamy {Updater.CurrentText}";
        Icon = AppIcon.Load();
        ClientSize = new Size(1040, 460);
        MinimumSize = new Size(820, 340);
        StartPosition = FormStartPosition.CenterScreen;

        _list.Columns.Add("Device", 220);
        _list.Columns.Add("Connection", 100);
        _list.Columns.Add("State", 150);
        _list.Columns.Add("Serial", 240);
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_rebuilding) return;
            UpdateButtons();
            _ = UpdateDetailsAsync();
        };
        _list.ItemActivate += async (_, _) => await ActivateSelectedAsync();
        _list.ContextMenuStrip = BuildContextMenu();

        var pairQr = new Button { Text = "Pair with QR", AutoSize = true };
        pairQr.Click += async (_, _) => await PairWithQrAsync();
        var pairCode = new Button { Text = "Pair with code", AutoSize = true };
        pairCode.Click += async (_, _) => await PairWithCodeAsync();
        var connect = new Button { Text = "Connect by IP", AutoSize = true };
        connect.Click += async (_, _) => await ConnectAsync();
        var settings = new Button { Text = "Settings", AutoSize = true };
        settings.Click += (_, _) => { using var w = new SettingsWindow(); w.ShowDialog(this); };
        _mirror.Click += async (_, _) => await ActivateSelectedAsync();
        _mirrorAll.Click += async (_, _) => await MirrorAllAsync();
        _wifi.Click += async (_, _) => await SwitchToWifiAsync();
        _files.Click += (_, _) => OpenFiles();
        _gallery.Click += (_, _) => OpenGallery();
        _apps.Click += (_, _) => OpenApps();

        _tips.SetToolTip(_mirrorAll, "Open a window for every ready phone");
        _tips.SetToolTip(pairQr, "Android 11+: pair by scanning a QR code from Wireless debugging");
        _tips.SetToolTip(pairCode, "Android 11+: pair with the six-digit code from Wireless debugging");
        _tips.SetToolTip(connect, "Connect to a phone already paired or in TCP mode, by its IP address and port");
        _tips.SetToolTip(_files, "Browse the selected phone's storage: download, upload, rename and delete files");
        _tips.SetToolTip(_wifi, "Move the selected USB phone to Wi-Fi: unplug it afterwards and keep mirroring");

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        Glyph.Apply(_mirror, Glyph.Mirror);
        Glyph.Apply(_mirrorAll, Glyph.MirrorAll);
        Glyph.Apply(_files, Glyph.Folder);
        Glyph.Apply(_gallery, Glyph.Gallery);
        _tips.SetToolTip(_gallery, "The selected phone's photos and videos by album: open, download, upload and delete");
        Glyph.Apply(_apps, Glyph.Apps);
        _tips.SetToolTip(_apps, "The selected phone's apps: open one on the phone or in its own window, force stop, clear data, uninstall, back up");
        Glyph.Apply(pairQr, Glyph.QrCode);
        Glyph.Apply(pairCode, Glyph.Key);
        Glyph.Apply(connect, Glyph.Link);
        Glyph.Apply(_wifi, Glyph.Wifi);
        Glyph.Apply(settings, Glyph.Settings);
        bar.Controls.AddRange([_mirror, _mirrorAll, _files, _gallery, _apps, pairQr, pairCode, connect, _wifi, settings]);

        Controls.Add(_list);
        Controls.Add(_details);
        Controls.Add(bar);
        Controls.Add(_status);

        _tray.Icon = AppIcon.Load();
        _tray.DoubleClick += (_, _) => { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); };
        _tray.BalloonTipClicked += async (_, _) =>
        {
            // Clicking a phone notification opens that phone's window, where it can be answered.
            if (_balloonSerial is { } serial && _devices.FirstOrDefault(d => d.Serial == serial && d.IsReady) is { } d)
                await MirrorAsync(d.Serial, d.DisplayName);
        };
        _poll.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            await RefreshAsync();
            _poll.Start();
            Updater.CleanupOld();
            PhoneFileDrag.CleanUp();
            if (AppSettings.Current.CheckForUpdates) await UpdateCheck.RunAsync(this, quiet: true);
        };
    }

    // ---- Rows ---------------------------------------------------------------------------------------------

    /// <summary>A list row: a device adb sees, or a saved Wi-Fi phone that is not connected.</summary>
    private sealed record Row(AdbDevice? Device, SavedDevice? Saved)
    {
        public string Key => Device?.Serial ?? Saved!.Endpoint;
    }

    private Row? Selected => _list.SelectedItems.Count == 1 ? (Row)_list.SelectedItems[0].Tag! : null;

    private void UpdateButtons()
    {
        var row = Selected;
        _mirror.Enabled = row is { Device.IsReady: true } || row is { Saved: not null };
        _mirror.Text = row is { Saved: not null, Device: null } ? "Connect" : "Mirror";
        _mirrorAll.Enabled = _devices.Count(d => d.IsReady) > 1;
        _wifi.Enabled = row is { Device: { IsReady: true, IsWireless: false } };
        _files.Enabled = _gallery.Enabled = _apps.Enabled = row is { Device.IsReady: true };
    }

    /// <summary>
    /// With "Show the phone's notifications on this PC" on, one watcher per ready phone; they stop when the phone
    /// goes or the setting is turned off.
    /// </summary>
    private void UpdateNotificationWatchers()
    {
        bool on = AppSettings.Current.PhoneNotifications;
        _tray.Visible = on;
        var ready = on ? _devices.Where(d => d.IsReady).ToDictionary(d => d.Serial) : [];
        foreach (var serial in _watchers.Keys.ToList())
        {
            if (ready.ContainsKey(serial) && !_watchers[serial].HasExited) continue;
            _watchers[serial].Dispose();
            _watchers.Remove(serial);
        }
        foreach (var (serial, device) in ready)
        {
            if (_watchers.ContainsKey(serial)) continue;
            var watcher = new NotificationWatcher(serial);
            watcher.Posted += (s, app, n) => BeginInvoke(() => ShowPhoneNotification(s, device.DisplayName, app, n));
            _watchers[serial] = watcher;
        }
    }

    private void ShowPhoneNotification(string serial, string phone, string app, Core.Adb.PhoneNotification n)
    {
        if (!AppSettings.Current.PhoneNotifications) return;
        _balloonSerial = serial;
        string title = n.Title.Length > 0 ? $"{app}: {n.Title}" : app;
        string text = n.Text.Length > 0 ? n.Text : n.Title;
        if (_devices.Count(d => d.IsReady) > 1) text += $"\n{phone}";
        _tray.ShowBalloonTip(6000, Clip(title, 63), Clip(text, 255), ToolTipIcon.None);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _devices = await AdbClient.Default.DevicesAsync();
            UpdateNotificationWatchers();
            RememberWirelessDevices();
            var saved = AppSettings.Current.SavedDevices.Where(s => !_devices.Any(d => d.Serial == s.Endpoint)).ToList();
            if (++_pollCount % SavedReconnectEvery == 1) ReconnectSaved(saved);

            var rows = _devices
                .Select(d => (Row: new Row(d, null), Cells: new[] { d.DisplayName, d.IsWireless ? "Wi-Fi" : "USB", _open.ContainsKey(d.Serial) ? "Mirroring" : StateText(d.State), d.Serial }))
                .Concat(saved.Select(s => (Row: new Row(null, s), Cells: new[] { s.Name, "Wi-Fi", _reconnecting.Contains(s.Endpoint) ? "Reconnecting" : "Saved, not connected", s.Endpoint })))
                .ToList();

            // Rebuild only when something changed: clearing the list drops the selection for a moment, which would
            // disable the buttons under the user's mouse and swallow their click.
            string signature = string.Join("\n", rows.Select(r => string.Join("|", r.Cells)));
            if (signature != _signature)
            {
                _signature = signature;
                var keep = Selected?.Key;
                _rebuilding = true;
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var (row, cells) in rows)
                {
                    var item = new ListViewItem(cells) { Tag = row };
                    if (row.Device is null) item.ForeColor = SystemColors.GrayText;
                    _list.Items.Add(item);
                    if (row.Key == keep) item.Selected = true;
                }
                if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
                _list.EndUpdate();
                _rebuilding = false;
            }
            else
            {
                // Same rows: refresh the Device objects behind them (state details) without touching the list.
                foreach (ListViewItem item in _list.Items)
                {
                    var key = ((Row)item.Tag!).Key;
                    if (rows.FirstOrDefault(r => r.Row.Key == key).Row is { } fresh) item.Tag = fresh;
                }
            }

            if (!_busy && DateTime.Now > _messageUntil)
            {
                _status.Text = _devices.Count == 0
                    ? "No phone found. Plug one in with USB debugging on, or pair over Wi-Fi."
                    : $"{_devices.Count} device(s). Double-click a phone to mirror it. Drop files or APKs on its window to send them.";
            }
            UpdateButtons();
            _ = UpdateDetailsAsync();
        }
        catch (Exception ex)
        {
            _status.Text = "adb: " + ex.Message;
            Log.Error(ex, "device list");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private Task UpdateDetailsAsync() =>
        Selected is { Device: { IsReady: true } d } ? _details.UpdateAsync(d.Serial, d.DisplayName) : _details.UpdateAsync(null, "");

    private static string StateText(string state) => state switch
    {
        "device" => "Ready",
        "unauthorized" => "Allow on the phone",
        "offline" => "Offline",
        _ => state,
    };

    /// <summary>Saves every ready phone connected by IP:port so it can be reconnected later.</summary>
    private void RememberWirelessDevices()
    {
        var settings = AppSettings.Current;
        bool changed = false;
        foreach (var d in _devices.Where(d => d.IsReady && AdbParsers.TryParseEndpoint(d.Serial, out _, out _) && d.Serial.Contains(':')))
        {
            var existing = settings.SavedDevices.FindIndex(s => s.Endpoint == d.Serial);
            var entry = new SavedDevice(d.Serial, d.DisplayName);
            if (existing < 0) { settings.SavedDevices.Add(entry); changed = true; }
            else if (settings.SavedDevices[existing] != entry) { settings.SavedDevices[existing] = entry; changed = true; }
        }
        if (changed) settings.Save();
    }

    private void ReconnectSaved(IEnumerable<SavedDevice> saved)
    {
        foreach (var s in saved)
        {
            if (!_reconnecting.Add(s.Endpoint)) continue;
            _ = Task.Run(async () =>
            {
                try { await AdbClient.Default.ConnectAsync(s.Endpoint); }
                catch (Exception ex)
                {
                    // An away phone fails every 10 s; log it once, not every time.
                    if (_reconnectLogged.Add(s.Endpoint)) Log.Info($"Reconnect {s.Endpoint}: {ex.Message}");
                }
                finally { BeginInvoke(() => _reconnecting.Remove(s.Endpoint)); }
            });
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var mirror = menu.Items.Add("Mirror", null, async (_, _) => await ActivateSelectedAsync());
        var files = menu.Items.Add("Browse files", null, (_, _) => OpenFiles());
        var gallery = menu.Items.Add("Gallery", null, (_, _) => OpenGallery());
        var apps = menu.Items.Add("Apps", null, (_, _) => OpenApps());
        var console = menu.Items.Add("Log and shell", null, (_, _) => { if (Selected is { Device: { IsReady: true } d }) FileManager.OpenConsole(d.Serial, d.DisplayName); });
        var wifi = menu.Items.Add("Switch to Wi-Fi", null, async (_, _) => await SwitchToWifiAsync());
        var disconnect = menu.Items.Add("Disconnect", null, async (_, _) => await DisconnectAsync());
        var forget = menu.Items.Add("Forget", null, (_, _) => Forget());
        menu.Opening += (_, e) =>
        {
            var row = Selected;
            if (row is null) { e.Cancel = true; return; }
            mirror.Text = row.Device is null ? "Connect" : "Mirror";
            mirror.Enabled = row.Device is null or { IsReady: true };
            files.Visible = gallery.Visible = apps.Visible = console.Visible = row.Device is { IsReady: true };
            wifi.Visible = row.Device is { IsReady: true, IsWireless: false };
            disconnect.Visible = row.Device is { IsWireless: true };
            forget.Visible = AppSettings.Current.SavedDevices.Any(s => s.Endpoint == row.Key);
        };
        return menu;
    }

    // ---- Mirroring ----------------------------------------------------------------------------------------

    private async Task ActivateSelectedAsync()
    {
        switch (Selected)
        {
            case { Device: { IsReady: true } d }:
                await MirrorAsync(d.Serial, d.DisplayName);
                break;
            case { Device: null, Saved: { } s }:
                await ConnectEndpointAsync(s.Endpoint);
                break;
            case { Device: { State: "unauthorized" } }:
                _status.Text = "Unlock the phone and tap Allow on the \"Allow USB debugging?\" prompt.";
                break;
        }
    }

    private void OpenFiles()
    {
        if (Selected is { Device: { IsReady: true } d }) FileManager.Open(d.Serial, d.DisplayName);
    }

    private void OpenApps()
    {
        if (Selected is { Device: { IsReady: true } d }) FileManager.OpenApps(d.Serial, d.DisplayName);
    }

    private void OpenGallery()
    {
        if (Selected is { Device: { IsReady: true } d }) FileManager.OpenGallery(d.Serial, d.DisplayName);
    }

    private async Task MirrorAllAsync()
    {
        foreach (var d in _devices.Where(d => d.IsReady && !_open.ContainsKey(d.Serial)).ToList())
            await MirrorAsync(d.Serial, d.DisplayName);
    }

    /// <summary>Starts a session and opens its window, or brings the phone's open window forward.</summary>
    public async Task MirrorAsync(string serial, string? displayName = null)
    {
        if (_open.TryGetValue(serial, out var existing))
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }
        var session = new MirrorSession(AdbClient.Default, serial, AppSettings.Current.ToServerOptions());
        SetBusy($"Starting {displayName ?? serial}");
        try
        {
            await session.StartAsync();
            Track(new SessionWindow(session), serial, displayName).Show();
            SetIdle($"Mirroring {session.DeviceName}.");
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            SetIdle("Could not start.");
            Log.Error(ex, "start session");
            MessageBox.Show(this, ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        await RefreshAsync();
    }

    /// <summary>Registers a phone window and brings its session back when the connection drops.</summary>
    private SessionWindow Track(SessionWindow window, string serial, string? displayName)
    {
        _open[serial] = window;
        window.FormClosed += (_, _) =>
        {
            // A reconnect replaces the window: only forget the phone if this is still its window.
            if (_open.TryGetValue(serial, out var current) && current == window) _open.Remove(serial);
            _ = RefreshAsync();
        };
        window.ConnectionLost += w => _ = ReconnectAsync(w, serial, displayName);
        return window;
    }

    /// <summary>
    /// The phone's connection dropped (Wi-Fi out of range, cable pulled, phone restarted). Keep its window with the
    /// last picture, and for up to two minutes look for the phone (reconnecting Wi-Fi ones); once it is back, start
    /// a new session in a new window at the same place and close the old one.
    /// </summary>
    private async Task ReconnectAsync(SessionWindow old, string serial, string? displayName)
    {
        Log.Info($"connection to {serial} lost; reconnecting");
        bool wireless = serial.Contains(':');
        var until = DateTime.UtcNow.AddMinutes(2);
        string why = "the phone did not come back within two minutes";
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(3000);
            if (old.IsDisposed) return; // the user closed it: stop trying
            try
            {
                if (wireless) await AdbClient.Default.ConnectAsync(serial);
                var devices = await AdbClient.Default.DevicesAsync();
                if (!devices.Any(d => d.Serial == serial && d.IsReady)) continue;
                var session = new MirrorSession(AdbClient.Default, serial, AppSettings.Current.ToServerOptions());
                try
                {
                    await session.StartAsync();
                }
                catch (Exception ex)
                {
                    await session.DisposeAsync();
                    Log.Info($"reconnect {serial}: {ex.Message}");
                    why = ex.Message;
                    continue;
                }
                if (old.IsDisposed) { await session.DisposeAsync(); return; }
                var window = Track(new SessionWindow(session), serial, displayName);
                window.StartPosition = FormStartPosition.Manual;
                var bounds = old.Bounds;
                var state = old.WindowState;
                window.Show();
                window.Bounds = bounds;
                if (state == FormWindowState.Maximized) window.WindowState = state;
                old.Close();
                Log.Info($"reconnected {serial}");
                await RefreshAsync();
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
            {
                why = ex.Message;
            }
        }
        if (!old.IsDisposed) old.ShowDisconnected(why);
    }

    // ---- Wi-Fi --------------------------------------------------------------------------------------------

    private async Task SwitchToWifiAsync()
    {
        Log.Info($"Switch to Wi-Fi clicked; selected: {Selected?.Key ?? "none"} ({_list.SelectedItems.Count} rows)");
        if (Selected is not { Device: { IsReady: true, IsWireless: false } d }) return;
        SetBusy($"Moving {d.DisplayName} to Wi-Fi");
        Log.Info($"Switching {d.Serial} to Wi-Fi");
        try
        {
            var ip = await AdbClient.Default.WifiAddressAsync(d.Serial);
            Log.Info($"Wi-Fi address of {d.Serial}: {ip ?? "none"}");
            if (ip is null) { SetIdle("Turn on Wi-Fi on the phone first (same network as this PC)."); return; }
            var r = await AdbClient.Default.TcpIpAsync(d.Serial, 5555);
            Log.Info($"tcpip: {r.All.Trim()}");
            if (!r.Ok) { SetIdle("The phone refused TCP mode: " + r.All); return; }
            string endpoint = ip + ":5555";
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                if (AdbParsers.IsConnectSuccess((await AdbClient.Default.ConnectAsync(endpoint)).All))
                {
                    SetIdle($"{d.DisplayName} is on Wi-Fi at {endpoint}. You can unplug the cable.");
                    await RefreshAsync();
                    return;
                }
            }
            SetIdle($"Could not reach {endpoint}. Check that the phone and this PC are on the same network.");
        }
        catch (Exception ex)
        {
            SetIdle("Switching to Wi-Fi failed: " + ex.Message);
            Log.Error(ex, "tcpip");
        }
    }

    private async Task DisconnectAsync()
    {
        if (Selected is not { Device: { IsWireless: true } d }) return;
        if (_open.TryGetValue(d.Serial, out var w)) w.Close();
        await AdbClient.Default.DisconnectAsync(d.Serial);
        await RefreshAsync();
    }

    private void Forget()
    {
        if (Selected is not { } row) return;
        AppSettings.Current.SavedDevices.RemoveAll(s => s.Endpoint == row.Key);
        AppSettings.Current.Save();
        _ = RefreshAsync();
    }

    private async Task ConnectAsync()
    {
        var endpoint = Prompt.Show(this, "Connect by IP", "Phone address (IP:port), for example from Wireless debugging on the phone:");
        if (endpoint is null) return;
        if (!AdbParsers.TryParseEndpoint(endpoint, out var host, out var port)) { _status.Text = "That is not an address like 192.168.1.20:41235."; return; }
        await ConnectEndpointAsync($"{host}:{port}");
    }

    private async Task ConnectEndpointAsync(string endpoint)
    {
        SetBusy($"Connecting to {endpoint}");
        var r = await AdbClient.Default.ConnectAsync(endpoint);
        SetIdle(AdbParsers.IsConnectSuccess(r.All) ? "Connected to " + endpoint : r.All.Trim());
        await RefreshAsync();
    }

    private async Task PairWithQrAsync()
    {
        using var dlg = new QrPairDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await WaitForWirelessAsync();
    }

    private async Task PairWithCodeAsync()
    {
        var endpoint = Prompt.Show(this, "Pair with code", "On the phone: Developer options > Wireless debugging > Pair device with pairing code.\nPairing address (IP:port):");
        if (endpoint is null) return;
        var code = Prompt.Show(this, "Pair with code", "Six-digit pairing code:");
        if (code is null) return;
        var r = await AdbClient.Default.PairAsync(endpoint, code.Trim());
        if (!AdbParsers.IsPairSuccess(r.All)) { _status.Text = "Pairing failed: " + r.All.Trim(); return; }
        await WaitForWirelessAsync();
    }

    /// <summary>After pairing, the phone advertises its connect port over mDNS and adb connects by itself.</summary>
    private async Task WaitForWirelessAsync()
    {
        SetBusy("Paired. Looking for the phone");
        int before = _devices.Count(d => d.IsWireless);
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000);
            await RefreshAsync();
            if (_devices.Count(d => d.IsWireless) > before) { SetIdle("Paired and connected. Double-click the phone to mirror it."); return; }
        }
        SetIdle("Paired. If the phone does not appear, use Connect by IP with the address shown under Wireless debugging.");
    }

    private bool _busy;
    private DateTime _messageUntil;

    private void SetBusy(string text) { _busy = true; _status.Text = text; }

    /// <summary>Shows a result for at least 8 seconds before the device count takes the status bar back.</summary>
    private void SetIdle(string text)
    {
        _busy = false;
        _status.Text = text;
        _messageUntil = DateTime.Now.AddSeconds(8);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _poll.Dispose();
        _tips.Dispose();
        base.OnFormClosed(e);
    }
}
