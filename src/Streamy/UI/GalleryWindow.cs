using System.Diagnostics;
using Streamy.Core.Files;
using Streamy.Core.Media;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// The phone's photos and videos by album, as a thumbnail grid. Open one on the PC, download a selection, upload
/// photos into the open album, or delete. Reads Android's media library over adb; nothing is installed on the phone.
/// </summary>
internal sealed class GalleryWindow : Form
{
    private readonly string _serial;
    private readonly ListBox _albums = new()
    {
        Dock = DockStyle.Left,
        Width = 230,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed,
    };
    private readonly GalleryGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly List<Button> _buttons = [];
    private Button _open = null!, _download = null!, _delete = null!;
    private readonly ThumbnailLoader _loader;
    private IReadOnlyList<MediaItem> _all = [];
    private IReadOnlyList<MediaAlbum> _albumList = [];
    private bool _busy;
    private string? _report;

    public GalleryWindow(string serial, string deviceName)
    {
        _serial = serial;
        Text = $"{deviceName} gallery - Streamy";
        Icon = AppIcon.Load();
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _loader = new ThumbnailLoader(serial, _grid.TileSize);
        _loader.Loaded += _grid.ThumbnailsArrived;
        _grid.Loader = _loader;
        _grid.SelectionChanged += UpdateState;
        _grid.ItemActivated += async item => await OpenAsync(item);
        _grid.DeleteRequested += async () => await DeleteSelectedAsync();
        _grid.ContextMenuStrip = BuildMenu();

        _albums.ItemHeight = (int)Math.Round(30 * DeviceDpi / 96.0);
        _albums.DrawItem += DrawAlbum;
        _albums.SelectedIndexChanged += (_, _) => ShowAlbum();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        Button("Refresh", Glyph.Refresh, async () => await LoadAsync(), bar);
        _open = Button("Open", Glyph.Forward, async () => { if (_grid.Selected is [var one]) await OpenAsync(one); }, bar);
        _download = Button("Download", Glyph.Download, async () => await DownloadSelectedAsync(), bar);
        Button("Upload", Glyph.Upload, async () => await UploadAsync(), bar);
        _delete = Button("Delete", Glyph.Delete, async () => await DeleteSelectedAsync(), bar);

        var split = new Splitter { Dock = DockStyle.Left, Width = 3, BackColor = Color.FromArgb(0x30, 0x30, 0x30) };
        Controls.Add(_grid);
        Controls.Add(split);
        Controls.Add(_albums);
        Controls.Add(bar);
        Controls.Add(_status);
        Shown += async (_, _) => await LoadAsync();
    }

    // ---- Loading ------------------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        if (_busy) return;
        string? keep = (_albums.SelectedItem as MediaAlbum)?.Name;
        SetBusy(true, "Reading the phone's photos and videos");
        try
        {
            _all = await AdbClient.Default.QueryMediaAsync(_serial);
            _albumList = MediaStore.Albums(_all);
            _albums.BeginUpdate();
            _albums.Items.Clear();
            foreach (var a in _albumList) _albums.Items.Add(a);
            _albums.EndUpdate();
            int index = Math.Max(0, _albumList.ToList().FindIndex(a => a.Name == keep));
            if (_albums.Items.Count > 0) _albums.SelectedIndex = index;
            if (_all.Count == 0) Report("No photos or videos on the phone");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "gallery");
            Report("Could not read the phone's gallery: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowAlbum()
    {
        if (_albums.SelectedItem is MediaAlbum album) _grid.Items = album.Items;
        _grid.Focus();
        UpdateState();
    }

    private void DrawAlbum(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var album = (MediaAlbum)_albums.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Color.FromArgb(0x00, 0x5A, 0x9E) : _albums.BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        var text = Rectangle.Inflate(e.Bounds, -10, 0);
        var fore = selected ? Color.White : _albums.ForeColor;
        TextRenderer.DrawText(e.Graphics, album.Name, Font, text, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, album.Items.Count.ToString("N0"), Font, text, selected ? Color.White : SystemColors.GrayText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    // ---- Actions ------------------------------------------------------------------------------------------

    /// <summary>Copies the photo or video to a temporary folder and opens it with the PC's app for it.</summary>
    private async Task OpenAsync(MediaItem item)
    {
        if (_busy) return;
        var temp = Path.Combine(Path.GetTempPath(), "Streamy", Safe(_serial));
        Directory.CreateDirectory(temp);
        SetBusy(true, $"Opening {item.Name}");
        try
        {
            await AdbClient.Default.PullAsync(_serial, item.Path, temp);
            Process.Start(new ProcessStartInfo(Path.Combine(temp, item.Name)) { UseShellExecute = true });
            Report($"Opened {item.Name}");
        }
        catch (Exception ex)
        {
            Report($"Could not open {item.Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DownloadSelectedAsync()
    {
        var items = _grid.Selected;
        if (items.Count == 0 || _busy) return;
        using var dlg = new FolderBrowserDialog
        {
            Description = "Download to",
            UseDescriptionForTitle = true,
            SelectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int ok = 0;
        var failures = new List<string>();
        SetBusy(true, "");
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                _status.Text = $"Downloading {items[i].Name} ({i + 1}/{items.Count})";
                try { await AdbClient.Default.PullAsync(_serial, items[i].Path, dlg.SelectedPath); ok++; }
                catch (Exception ex) { failures.Add($"{items[i].Name}: {ex.Message}"); }
            }
        }
        finally
        {
            SetBusy(false);
        }
        Finish(failures, failures.Count == 0 ? $"Downloaded {Count(ok)} to {SaveFolders.Short(dlg.SelectedPath)}" : $"{ok} downloaded, {failures.Count} failed");
    }

    /// <summary>
    /// Uploads into the open album's folder (Pictures\Streamy for "All" and "Videos"), then asks the phone to
    /// index the files so they appear in its gallery at once.
    /// </summary>
    private async Task UploadAsync()
    {
        if (_busy) return;
        string folder = UploadFolder();
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = $"Upload to {folder}",
            Filter = "Photos and videos|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.heic;*.heif;*.bmp;*.mp4;*.mov;*.mkv;*.webm;*.3gp|All files|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int ok = 0;
        var failures = new List<string>();
        SetBusy(true, "");
        try
        {
            await AdbClient.Default.MakeFolderAsync(_serial, folder);
            for (int i = 0; i < dlg.FileNames.Length; i++)
            {
                var file = dlg.FileNames[i];
                _status.Text = $"Uploading {Path.GetFileName(file)} ({i + 1}/{dlg.FileNames.Length})";
                try
                {
                    await AdbClient.Default.PushIntoAsync(_serial, file, folder);
                    await AdbClient.Default.ScanMediaAsync(_serial, PhoneFiles.Combine(folder, Path.GetFileName(file)));
                    ok++;
                }
                catch (Exception ex) { failures.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        finally
        {
            SetBusy(false);
        }
        await Task.Delay(1500); // the scanner indexes in the background
        await LoadAsync();
        Finish(failures, failures.Count == 0 ? $"Uploaded {Count(ok)} to {folder}" : $"{ok} uploaded, {failures.Count} failed");
    }

    private string UploadFolder()
    {
        if (_albums.SelectedItem is MediaAlbum { Name: not (MediaStore.AllAlbum or MediaStore.VideosAlbum) } album && album.Items.Count > 0)
            return album.Items.GroupBy(i => i.Folder).OrderByDescending(g => g.Count()).First().Key;
        return "/sdcard/Pictures/Streamy";
    }

    private async Task DeleteSelectedAsync()
    {
        var items = _grid.Selected;
        if (items.Count == 0 || _busy) return;
        string what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"these {Count(items.Count)}";
        if (MessageBox.Show(this, $"Delete {what} from the phone? They are removed for good, not moved to the phone's trash.",
                "Streamy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        SetBusy(true, $"Deleting {Count(items.Count)}");
        try
        {
            await AdbClient.Default.DeleteMediaAsync(_serial, items);
            Report($"Deleted {Count(items.Count)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "gallery delete");
            Report("Could not delete: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        await LoadAsync();
    }

    // ---- Plumbing -----------------------------------------------------------------------------------------

    private void Finish(List<string> failures, string summary)
    {
        Report(summary);
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n", failures.Take(20)), "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        if (text is not null) _status.Text = text;
        UseWaitCursor = busy;
        UpdateState();
    }

    private void Report(string text)
    {
        _report = text;
        UpdateState();
    }

    private void UpdateState()
    {
        var sel = _grid.Selected;
        foreach (var b in _buttons) b.Enabled = !_busy;
        _open.Enabled = !_busy && sel.Count == 1;
        _download.Enabled = _delete.Enabled = !_busy && sel.Count > 0;
        if (_busy) return;
        int photos = _grid.Items.Count(i => !i.IsVideo), videos = _grid.Items.Count - photos;
        string counts = $"{photos:N0} photos, {videos:N0} videos" +
            (sel.Count > 0 ? $", {sel.Count:N0} selected ({PhoneFiles.FormatSize(sel.Sum(i => i.Size))})" : "");
        if (sel is [var one]) counts += $". {one.Name}, {one.Taken.ToLocalTime():yyyy-MM-dd HH:mm}" + (one.Width > 0 ? $", {one.Width} × {one.Height}" : "");
        _status.Text = _report is null ? counts : $"{_report}. {counts}";
        _report = null;
    }

    private static string Count(int n) => n == 1 ? "1 item" : $"{n:N0} items";

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    private Button Button(string text, string glyph, Func<Task> onClick, Control parent)
    {
        var b = new Button { Text = text, AutoSize = true };
        Glyph.Apply(b, glyph);
        b.Click += async (_, _) => await onClick();
        parent.Controls.Add(b);
        _buttons.Add(b);
        return b;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Open", null, async (_, _) => { if (_grid.Selected is [var one]) await OpenAsync(one); });
        var download = menu.Items.Add("Download", null, async (_, _) => await DownloadSelectedAsync());
        var delete = menu.Items.Add("Delete", null, async (_, _) => await DeleteSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Select all", null, (_, _) => _grid.SelectAll());
        menu.Opening += (_, e) =>
        {
            var sel = _grid.Selected;
            open.Enabled = sel.Count == 1;
            download.Enabled = delete.Enabled = sel.Count > 0;
            if (_busy) e.Cancel = true;
        };
        return menu;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _loader.Loaded -= _grid.ThumbnailsArrived;
        _loader.Dispose();
        base.OnFormClosed(e);
    }
}
