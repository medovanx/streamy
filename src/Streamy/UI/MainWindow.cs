using Streamy.Core.Adb;
using Streamy.Core.Server;
using Streamy.Services;
using Streamy.Session;

namespace Streamy.UI;

/// <summary>The device list: USB and Wi-Fi phones adb can see, with connect, pair and mirror actions.</summary>
internal sealed class MainWindow : Form
{
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
    private readonly CheckBox _showTouches = new() { Text = "Show touches", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private bool _refreshing;

    public MainWindow()
    {
        Text = $"Streamy {Application.ProductVersion.Split('+')[0]}";
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterScreen;

        _list.Columns.Add("Device", 220);
        _list.Columns.Add("Connection", 110);
        _list.Columns.Add("State", 110);
        _list.Columns.Add("Serial", 180);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.ItemActivate += async (_, _) => await MirrorSelectedAsync();

        var connect = new Button { Text = "Connect by IP...", AutoSize = true };
        connect.Click += async (_, _) => await ConnectAsync();
        var pair = new Button { Text = "Pair with code...", AutoSize = true };
        pair.Click += async (_, _) => await PairAsync();
        _mirror.Click += async (_, _) => await MirrorSelectedAsync();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        _showTouches.Checked = AppSettings.Current.ShowTouches;
        _showTouches.CheckedChanged += (_, _) =>
        {
            AppSettings.Current.ShowTouches = _showTouches.Checked;
            AppSettings.Current.Save();
        };
        _tips.SetToolTip(_showTouches,
            "Show a dot on the phone's screen wherever it is touched.\n" +
            "Applies to phones you start mirroring after changing it; the phone's own setting is restored when mirroring ends.");
        bar.Controls.AddRange([_mirror, connect, pair, _showTouches]);

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(_status);

        _poll.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); _poll.Start(); };
    }

    private AdbDevice? Selected => _list.SelectedItems.Count == 1 ? (AdbDevice)_list.SelectedItems[0].Tag! : null;

    private void UpdateButtons() => _mirror.Enabled = Selected is { IsReady: true };

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var devices = await AdbClient.Default.DevicesAsync();
            var keep = Selected?.Serial;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var d in devices)
            {
                var item = new ListViewItem([d.DisplayName, d.IsWireless ? "Wi-Fi" : "USB", StateText(d.State), d.Serial]) { Tag = d };
                _list.Items.Add(item);
                if (d.Serial == keep) item.Selected = true;
            }
            _list.EndUpdate();
            _status.Text = devices.Count == 0
                ? "No phone found. Plug one in with USB debugging on, or pair over Wi-Fi."
                : $"{devices.Count} device(s). Double-click a phone to mirror it.";
            UpdateButtons();
        }
        catch (Exception ex)
        {
            _status.Text = "adb: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string StateText(string state) => state switch
    {
        "device" => "Ready",
        "unauthorized" => "Allow on phone",
        "offline" => "Offline",
        _ => state,
    };

    private async Task MirrorSelectedAsync()
    {
        if (Selected is { IsReady: true } device) await MirrorAsync(device.Serial, device.DisplayName);
    }

    /// <summary>Starts a session and opens its window. Also used by the --mirror SERIAL switch.</summary>
    public async Task MirrorAsync(string serial, string? displayName = null)
    {
        var session = new MirrorSession(AdbClient.Default, serial, new ServerOptions
        {
            Scid = ServerOptions.NewScid(),
            ShowTouches = AppSettings.Current.ShowTouches,
        });
        _status.Text = $"Starting {displayName ?? serial}...";
        try
        {
            await session.StartAsync();
            new SessionWindow(session).Show();
            _status.Text = $"Mirroring {session.DeviceName}.";
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            _status.Text = "Could not start.";
            MessageBox.Show(this, ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ConnectAsync()
    {
        var endpoint = Prompt.Show(this, "Connect by IP", "Phone address (IP:port), from Wireless debugging on the phone:");
        if (endpoint is null) return;
        if (!AdbParsers.TryParseEndpoint(endpoint, out _, out _)) { _status.Text = "That is not an address like 192.168.1.20:41235."; return; }
        var r = await AdbClient.Default.ConnectAsync(endpoint);
        _status.Text = AdbParsers.IsConnectSuccess(r.All) ? "Connected to " + endpoint : r.All;
        await RefreshAsync();
    }

    private async Task PairAsync()
    {
        var endpoint = Prompt.Show(this, "Pair with code", "On the phone: Developer options > Wireless debugging > Pair device with pairing code.\nPairing address (IP:port):");
        if (endpoint is null) return;
        var code = Prompt.Show(this, "Pair with code", "Six-digit pairing code:");
        if (code is null) return;
        var r = await AdbClient.Default.PairAsync(endpoint, code.Trim());
        if (!AdbParsers.IsPairSuccess(r.All)) { _status.Text = "Pairing failed: " + r.All; return; }
        _status.Text = "Paired. Looking for the phone...";

        // After pairing, the phone advertises its connect port over mDNS; adb usually connects by itself.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000);
            await RefreshAsync();
            if (_list.Items.Cast<ListViewItem>().Any(it => ((AdbDevice)it.Tag!).IsWireless)) return;
        }
        _status.Text = "Paired. Use Connect by IP with the address shown under Wireless debugging.";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Dispose();
        _tips.Dispose();
        base.OnFormClosed(e);
    }
}
