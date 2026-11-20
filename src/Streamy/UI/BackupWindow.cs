using System.Diagnostics;
using Streamy.Core.Files;
using Streamy.Core.Media;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// A gallery backup while it runs: what is being copied, a progress bar with sizes and speed, and Cancel. Copies
/// only what the folder does not have yet, so running it again later is quick.
/// </summary>
internal sealed class BackupWindow : Form
{
    private readonly string _serial;
    private readonly IReadOnlyList<MediaItem> _items;
    private readonly string _folder;
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 1000 };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI Semibold", 11f), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _detail = new() { Dock = DockStyle.Top, Height = 44, TextAlign = ContentAlignment.MiddleLeft };
    private readonly LinkLabel _where = new() { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true };
    private readonly Button _open = new() { Text = "Open folder", AutoSize = true };
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _running = true;

    /// <summary>Raised when the backup ends (finished, failed or cancelled).</summary>
    public event Action<string>? Finished;

    public BackupWindow(string serial, string deviceName, IReadOnlyList<MediaItem> items, string folder)
    {
        _serial = serial;
        _items = items;
        _folder = folder;
        Text = $"Backing up {deviceName} - Streamy";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 200);
        Padding = new Padding(16, 10, 16, 12);

        _title.Text = $"Backing up {items.Count:N0} photos and videos";
        _detail.Text = "Checking what the folder already has";
        _where.Text = folder;
        _where.LinkClicked += (_, _) => OpenFolder();
        Glyph.Apply(_cancel, Glyph.Cancel);
        Glyph.Apply(_open, Glyph.FolderOpen);
        _open.Click += (_, _) => OpenFolder();
        _cancel.Click += (_, _) =>
        {
            if (!_running) { Close(); return; }
            _cancel.Enabled = false;
            _detail.Text = "Stopping after the current files";
            _cts.Cancel();
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_open);
        Controls.Add(_detail);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 });
        Controls.Add(_bar);
        Controls.Add(_where);
        Controls.Add(_title);
        Controls.Add(buttons);
        Shown += async (_, _) => await RunAsync();
    }

    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { }
    }

    private async Task RunAsync()
    {
        string summary;
        try
        {
            var r = await GalleryBackup.RunAsync(_serial, _items, _folder, new Progress<GalleryBackup.Progress>(Show), _cts.Token);
            summary = r.Copied == 0 && r.Failures.Count == 0
                ? $"Already up to date: all {r.Skipped:N0} photos and videos were backed up before."
                : $"Copied {r.Copied:N0} new ({PhoneFiles.FormatSize(r.Bytes)}), {r.Skipped:N0} were already there"
                  + (r.Failures.Count > 0 ? $", {r.Failures.Count:N0} failed" : "") + ".";
            if (r.Failures.Count > 0) Log.Info("backup failures:\n" + string.Join("\n", r.Failures.Take(50)));
            _title.Text = r.Failures.Count == 0 ? "Backup complete" : "Backup finished with errors";
            _bar.Value = _bar.Maximum;
        }
        catch (OperationCanceledException)
        {
            summary = "Backup stopped. What was copied stays; the next backup carries on from there.";
            _title.Text = "Backup stopped";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "backup");
            summary = "Backup failed: " + ex.Message;
            _title.Text = "Backup failed";
        }
        _running = false;
        _detail.Text = summary;
        _cancel.Text = "Close";
        _cancel.Enabled = true;
        Glyph.Apply(_cancel, Glyph.Check);
        Finished?.Invoke(summary);
    }

    private void Show(GalleryBackup.Progress p)
    {
        if (IsDisposed || !_running) return;
        _bar.Value = p.Bytes > 0 ? (int)Math.Clamp(p.BytesDone * 1000 / p.Bytes, 0, 1000) : 0;
        double seconds = Math.Max(1, _clock.Elapsed.TotalSeconds);
        string speed = p.BytesDone > 0 ? $", {PhoneFiles.FormatSize((long)(p.BytesDone / seconds))}/s" : "";
        string left = "";
        if (p.BytesDone > 1 << 20 && p.Bytes > p.BytesDone)
        {
            var eta = TimeSpan.FromSeconds((p.Bytes - p.BytesDone) / (p.BytesDone / seconds));
            left = eta.TotalMinutes >= 1 ? $", about {Math.Ceiling(eta.TotalMinutes):0} min left" : ", under a minute left";
        }
        _title.Text = p.Files == 0 ? "Nothing new to copy" : $"Copying {p.Files:N0} new photos and videos";
        _detail.Text = $"{p.FilesDone:N0} of {p.Files:N0} files, {PhoneFiles.FormatSize(p.BytesDone)} of {PhoneFiles.FormatSize(p.Bytes)}{speed}{left}"
            + (p.Current.Length > 0 ? $"\n{p.Current}" : "");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running)
        {
            // Closing the window stops the backup rather than leaving it running unseen.
            _cts.Cancel();
        }
        base.OnFormClosing(e);
    }
}
