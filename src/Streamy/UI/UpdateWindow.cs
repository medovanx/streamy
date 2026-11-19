using Streamy.Core.Files;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// Shows an update while it happens: a progress bar with megabytes and speed while downloading, then verifying
/// and installing. Cancel stops the download and leaves the running version as it is.
/// </summary>
internal sealed class UpdateWindow : Form
{
    private readonly Updater.Release _release;
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 1000 };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI Semibold", 11f), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _detail = new() { Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTime _started = DateTime.UtcNow;

    /// <summary>True once the new version was installed and started; the caller exits Streamy.</summary>
    public bool Installed { get; private set; }

    public string? Error { get; private set; }

    public UpdateWindow(Updater.Release release)
    {
        _release = release;
        Text = "Updating Streamy";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 150);
        Padding = new Padding(16, 10, 16, 12);

        _title.Text = $"Updating to Streamy {release.Version.ToString(3)}";
        _detail.Text = "Connecting to GitHub";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        Glyph.Apply(_cancel, Glyph.Cancel);
        buttons.Controls.Add(_cancel);
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _detail.Text = "Cancelling";
            _cts.Cancel();
        };
        var spacer = new Panel { Dock = DockStyle.Top, Height = 8 };
        Controls.Add(_detail);
        Controls.Add(spacer);
        Controls.Add(_bar);
        Controls.Add(_title);
        Controls.Add(buttons);
        CancelButton = _cancel;
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var progress = new Progress<Updater.Progress>(Show);
        try
        {
            await Updater.InstallAsync(_release, progress, _cts.Token);
            Installed = true;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            DialogResult = DialogResult.Cancel;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            DialogResult = DialogResult.Abort;
        }
    }

    private void Show(Updater.Progress p)
    {
        switch (p.Phase)
        {
            case Updater.Phase.Downloading when p.Total > 0:
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = (int)Math.Clamp(p.Done * 1000 / p.Total, 0, 1000);
                double seconds = Math.Max(0.5, (DateTime.UtcNow - _started).TotalSeconds);
                string speed = PhoneFiles.FormatSize((long)(p.Done / seconds)) + "/s";
                _detail.Text = $"Downloading {PhoneFiles.FormatSize(p.Done)} of {PhoneFiles.FormatSize(p.Total)} ({p.Done * 100 / p.Total}%), {speed}";
                break;
            case Updater.Phase.Downloading:
                _bar.Style = ProgressBarStyle.Marquee;
                _detail.Text = $"Downloading {PhoneFiles.FormatSize(p.Done)}";
                break;
            case Updater.Phase.Verifying:
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Value = _bar.Maximum;
                _detail.Text = "Checking the download against GitHub's checksum";
                break;
            case Updater.Phase.Installing:
                _cancel.Enabled = false;
                _detail.Text = "Installing and restarting";
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing with the X while it runs means cancel; wait for the download to stop before closing.
        if (DialogResult == DialogResult.None)
        {
            e.Cancel = true;
            if (_cancel.Enabled) _cancel.PerformClick();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Dispose();
        base.OnFormClosed(e);
    }
}
