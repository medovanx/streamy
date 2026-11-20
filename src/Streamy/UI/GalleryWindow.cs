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
    private readonly GalleryDetails _details;
    private readonly string _deviceName;
    private IReadOnlyList<MediaItem> _all = [];
    private IReadOnlyList<MediaAlbum> _albumList = [];
    private bool _busy;
    private string? _report;
    private readonly LocationIndex _locations;
    private CancellationTokenSource _scanCts = new();
    private string? _scanStatus;

    public GalleryWindow(string serial, string deviceName)
    {
        _serial = serial;
        _deviceName = deviceName;
        Text = $"{deviceName} gallery - Streamy";
        Icon = AppIcon.Load();
        ClientSize = new Size(1100, 700);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterScreen;

        _loader = new ThumbnailLoader(serial, _grid.TileSize);
        _loader.Loaded += _grid.ThumbnailsArrived;
        _grid.Loader = _loader;
        _locations = new LocationIndex(serial);
        _details = new GalleryDetails(serial, _loader, _locations);
        _grid.SelectionChanged += () => { UpdateState(); _details.ShowSelection(_grid.Selected); };
        _grid.ItemActivated += View;
        _grid.DeleteRequested += async () => await DeleteSelectedAsync();
        _grid.DragOut += items =>
        {
            if (_busy || items.Count == 0) return;
            var data = new PhoneFileDrag(_serial, items);
            _status.Text = $"Drop to copy {Count(items.Count)} to the PC";
            bool dropped = data.Run();
            _status.Text = dropped ? $"Copied {Count(items.Count)} to where they were dropped" : "";
            UpdateState();
        };
        _grid.ContextMenuStrip = BuildMenu();

        _albums.ItemHeight = (int)Math.Round(30 * DeviceDpi / 96.0);
        _albums.DrawItem += DrawAlbum;
        _albums.SelectedIndexChanged += (_, _) => ShowAlbum();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        Button("Refresh", Glyph.Refresh, async () => await LoadAsync(), bar);
        _open = Button("View", Glyph.Gallery, () => { if (_grid.Selected is [var one]) View(one); return Task.CompletedTask; }, bar);
        _download = Button("Download", Glyph.Download, async () => await DownloadSelectedAsync(), bar);
        Button("Upload", Glyph.Upload, async () => await UploadAsync(), bar);
        _delete = Button("Delete", Glyph.Delete, async () => await DeleteSelectedAsync(), bar);
        var map = Button("Map", Glyph.Map, () => { ShowMap(); return Task.CompletedTask; }, bar);
        var backup = Button("Back up", Glyph.Backup, () => { BackUp(changeFolder: false); return Task.CompletedTask; }, bar);
        var backupMenu = new ContextMenuStrip();
        backupMenu.Items.Add("Back up now", null, (_, _) => BackUp(changeFolder: false));
        backupMenu.Items.Add("Change backup folder", null, (_, _) => BackUp(changeFolder: true));
        backup.ContextMenuStrip = backupMenu;
        _tips.SetToolTip(map, "Where the photos and videos were taken, on a map (tiles from OpenStreetMap)");
        _tips.SetToolTip(backup, "Copy every photo and video to a folder on this PC; later backups copy only what is new (right-click: change the folder)");

        var split = new Splitter { Dock = DockStyle.Left, Width = 3, BackColor = Color.FromArgb(0x30, 0x30, 0x30) };
        Controls.Add(_grid);
        Controls.Add(_details);
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
            FillAlbums(keep);
            if (_all.Count == 0) Report("No photos or videos on the phone");
            _ = ScanPlacesAsync();
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

    /// <summary>The folder albums, then the places found so far. Keeps the selected album when it still exists.</summary>
    private void FillAlbums(string? keep)
    {
        _albumList = [.. MediaStore.Albums(_all), .. Places.Albums(_all, _locations.Place)];
        _albums.BeginUpdate();
        _albums.Items.Clear();
        foreach (var a in _albumList) _albums.Items.Add(a);
        _albums.EndUpdate();
        int index = _albumList.ToList().FindIndex(a => a.Name == keep && a.Kind != AlbumKind.Heading);
        _suppressAlbumChange = index >= 0 && _albums.SelectedIndex == -1 && keep is not null && _grid.Items.Count > 0
            && _albumList[index].Items.Count == _grid.Items.Count;
        if (_albums.Items.Count > 0) _albums.SelectedIndex = Math.Max(0, index);
        _suppressAlbumChange = false;
    }

    private bool _suppressAlbumChange;

    /// <summary>
    /// Reads where new photos and videos were taken, in the background, then adds the Places section.
    /// A second visit only reads items added since.
    /// </summary>
    private async Task ScanPlacesAsync()
    {
        _scanCts.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var items = _all;
        try
        {
            await _locations.ScanAsync(items, new Progress<(int Done, int Total)>(p =>
            {
                _scanStatus = p.Done < p.Total ? $"Finding places: {p.Done:N0} of {p.Total:N0}" : null;
                if (!_busy) UpdateState();
            }), ct);
            if (ct.IsCancellationRequested || IsDisposed) return;
            _scanStatus = null;
            FillAlbums((_albums.SelectedItem as MediaAlbum)?.Name);
            _details.ShowSelection(_grid.Selected);
            UpdateState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "places");
            _scanStatus = null;
        }
    }

    private void ShowAlbum()
    {
        if (_albums.SelectedItem is MediaAlbum { Kind: AlbumKind.Heading })
        {
            // Headings are not albums: move to the first entry under it.
            if (_albums.SelectedIndex + 1 < _albums.Items.Count) _albums.SelectedIndex++;
            return;
        }
        if (_suppressAlbumChange) return;
        if (_albums.SelectedItem is MediaAlbum album) _grid.Items = album.Items;
        _grid.Focus();
        UpdateState();
    }

    private void DrawAlbum(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var album = (MediaAlbum)_albums.Items[e.Index];
        if (album.Kind == AlbumKind.Heading)
        {
            using var bg = new SolidBrush(_albums.BackColor);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var bold = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, album.Name.ToUpperInvariant(), bold, Rectangle.Inflate(e.Bounds, -10, 0), SystemColors.GrayText,
                TextFormatFlags.Bottom | TextFormatFlags.Left);
            return;
        }
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Color.FromArgb(0x00, 0x5A, 0x9E) : _albums.BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        var text = Rectangle.Inflate(e.Bounds, -10, 0);
        if (album.Kind == AlbumKind.Place)
        {
            // A small pin before place names.
            int size = (int)Math.Round(14 * DeviceDpi / 96.0);
            using var pin = Glyph.Image(Glyph.MapPin, selected ? Color.White : SystemColors.GrayText, size);
            e.Graphics.DrawImage(pin, text.X, e.Bounds.Y + (e.Bounds.Height - size) / 2);
            text = new Rectangle(text.X + size + 6, text.Y, text.Width - size - 6, text.Height);
        }
        var fore = selected ? Color.White : _albums.ForeColor;
        TextRenderer.DrawText(e.Graphics, album.Name, Font, text, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, album.Items.Count.ToString("N0"), Font, text, selected ? Color.White : SystemColors.GrayText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    // ---- Actions ------------------------------------------------------------------------------------------

    /// <summary>Opens the built-in viewer at this item, to swipe through the open album.</summary>
    private void View(MediaItem item)
    {
        try
        {
            var items = _grid.Items;
            int index = items.ToList().FindIndex(i => i.Id == item.Id);
            var viewer = new MediaViewer(_serial, _deviceName, items, Math.Max(0, index), _loader, _locations);
            viewer.Deleted += () => _reloadAfterViewer = true;
            viewer.FormClosed += async (_, _) => { if (_reloadAfterViewer) { _reloadAfterViewer = false; await LoadAsync(); } };
            viewer.Show(this);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "open viewer");
            Report("Could not open the viewer: " + ex.Message);
        }
    }

    private bool _reloadAfterViewer;

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

    private readonly ToolTip _tips = new();
    private MapWindow? _map;

    private void ShowMap()
    {
        if (_map is { IsDisposed: false }) { _map.Activate(); return; }
        var located = _all.Select(i => (Item: i, Point: _locations.Point(i))).Where(l => l.Point is not null).Select(l => (l.Item, l.Point!.Value)).ToList();
        if (located.Count == 0)
        {
            Report(_scanStatus is not null ? "Still finding where photos were taken; try the map again in a moment" : "No photos or videos with a location");
            return;
        }
        if (!MapWindow.Consent(this)) return;
        _map = new MapWindow(_deviceName, located, _loader, ShowItems);
        _map.Show(this);
    }

    /// <summary>Shows a set of items (a map pin's) in the grid, outside the album list.</summary>
    private void ShowItems(IReadOnlyList<MediaItem> items, string label)
    {
        _suppressAlbumChange = true;
        _albums.ClearSelected();
        _suppressAlbumChange = false;
        _grid.Items = items;
        Report(label);
        Activate();
    }

    private BackupWindow? _backup;

    /// <summary>Backs up the whole library (every album) to this phone's backup folder, asking for it the first time.</summary>
    private void BackUp(bool changeFolder)
    {
        if (_backup is { IsDisposed: false })
        {
            _backup.Activate();
            return;
        }
        if (_all.Count == 0) { Report("Nothing to back up yet"); return; }
        var folders = AppSettings.Current.BackupFolders;
        if (changeFolder || !folders.TryGetValue(_serial, out var folder))
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = $"Back up {_deviceName}'s photos and videos to",
                UseDescriptionForTitle = true,
                SelectedPath = folders.TryGetValue(_serial, out var old) ? old : GalleryBackup.DefaultFolder(_deviceName),
            };
            Directory.CreateDirectory(dlg.SelectedPath);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            folder = dlg.SelectedPath;
            folders[_serial] = folder;
            AppSettings.Current.Save();
        }
        _backup = new BackupWindow(_serial, _deviceName, _all, folder);
        _backup.Finished += summary => Report(summary);
        _backup.Show(this);
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
        if (_scanStatus is not null) counts += ". " + _scanStatus;
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
        var open = menu.Items.Add("View", null, (_, _) => { if (_grid.Selected is [var one]) View(one); });
        var openApp = menu.Items.Add("Open in app", null, async (_, _) => { if (_grid.Selected is [var one]) await OpenAsync(one); });
        var download = menu.Items.Add("Download", null, async (_, _) => await DownloadSelectedAsync());
        var delete = menu.Items.Add("Delete", null, async (_, _) => await DeleteSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Select all", null, (_, _) => _grid.SelectAll());
        menu.Opening += (_, e) =>
        {
            var sel = _grid.Selected;
            open.Enabled = openApp.Enabled = sel.Count == 1;
            download.Enabled = delete.Enabled = sel.Count > 0;
            if (_busy) e.Cancel = true;
        };
        return menu;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _scanCts.Cancel();
        _loader.Loaded -= _grid.ThumbnailsArrived;
        _loader.Dispose();
        base.OnFormClosed(e);
    }
}
