using System.Diagnostics;
using System.Globalization;
using Streamy.Core.Files;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// Browse a phone's storage: open folders, download files and folders to the PC, upload by button or by dropping
/// files from Explorer, create folders, rename and delete. Double-clicking a file opens it on the PC.
/// Everything goes over adb, so it works over USB and Wi-Fi with nothing installed on the phone.
/// </summary>
internal sealed class FileManagerWindow : Form
{
    private readonly string _serial;
    private readonly ShellIcons _icons = new();
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false,
        LabelEdit = true,
        AllowDrop = true,
        BorderStyle = BorderStyle.None,
    };
    private readonly TextBox _path = new() { Width = 420, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly List<Button> _actions = [];
    private Button _up = null!, _download = null!, _rename = null!, _delete = null!;
    private string _folder = PhoneFiles.Home;
    private IReadOnlyList<PhoneFile> _files = [];
    private bool _busy;
    private bool _editing;
    private int _sortColumn;
    private bool _sortDescending;

    public FileManagerWindow(string serial, string deviceName)
    {
        _serial = serial;
        Text = $"{deviceName} files - Streamy";
        Icon = AppIcon.Load();
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        _list.SmallImageList = _icons.List;
        _list.Columns.Add("Name", 380);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Modified", 150);
        _list.Columns.Add("Type", 120);
        _list.ItemActivate += async (_, _) => await OpenSelectedAsync();
        _list.SelectedIndexChanged += (_, _) => UpdateState();
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.BeforeLabelEdit += (_, e) => { if (_busy) e.CancelEdit = true; else _editing = true; };
        _list.AfterLabelEdit += async (_, e) => { _editing = false; await RenameAsync(e); };
        _list.DragEnter += (_, e) => e.Effect = !_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _list.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await UploadAsync(paths); };
        _list.ContextMenuStrip = BuildMenu();

        var nav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 6, 6, 0), WrapContents = false };
        _up = Action("Up", async () => await GoUpAsync(), nav, Glyph.Up);
        Action("Refresh", async () => await LoadAsync(_folder), nav, Glyph.Refresh);
        nav.Controls.Add(_path);
        Action("Go", async () => await NavigateAsync(_path.Text), nav, Glyph.Forward);
        var places = new Button { Text = "Places", AutoSize = true };
        var placesMenu = new ContextMenuStrip();
        foreach (var (label, path) in PhoneFiles.Places) placesMenu.Items.Add(label, null, async (_, _) => await NavigateAsync(path));
        places.Click += (_, _) => placesMenu.Show(places, new Point(0, places.Height));
        Glyph.Apply(places, Glyph.Places);
        places.AutoSize = true;
        nav.Controls.Add(places);
        _actions.Add(places);
        _path.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await NavigateAsync(_path.Text);
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 0, 6, 6), WrapContents = false };
        Action("Upload", async () => await PickAndUploadAsync(), bar, Glyph.Upload);
        _download = Action("Download", async () => await DownloadSelectedAsync(), bar, Glyph.Download);
        Action("New folder", async () => await NewFolderAsync(), bar, Glyph.NewFolder);
        _rename = Action("Rename", BeginRename, bar, Glyph.Rename);
        _delete = Action("Delete", async () => await DeleteSelectedAsync(), bar, Glyph.Delete);

        Controls.Add(_list);
        Controls.Add(bar);
        Controls.Add(nav);
        Controls.Add(_status);
        Shown += async (_, _) => await LoadAsync(PhoneFiles.Home);
    }

    // ---- Navigation ---------------------------------------------------------------------------------------

    private async Task NavigateAsync(string path)
    {
        path = PhoneFiles.Normalize(path.Trim());
        await LoadAsync(path);
    }

    private async Task GoUpAsync()
    {
        if (PhoneFiles.Parent(_folder) is { } parent) await LoadAsync(parent);
    }

    /// <summary>Lists a folder and shows it. Returns false (and keeps the current folder) if the phone refused.</summary>
    private async Task<bool> LoadAsync(string path)
    {
        SetBusy(true, $"Opening {path}");
        try
        {
            var files = await AdbClient.Default.ListAsync(_serial, path);
            _folder = path;
            _files = files;
            _path.Text = path;
            Fill();
            return true;
        }
        catch (Exception ex)
        {
            _path.Text = _folder;
            Report($"Could not open {path}: {ex.Message}");
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Fill()
    {
        var keep = Selected.Select(f => f.Name).ToHashSet();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var f in Sorted(_files))
        {
            var item = new ListViewItem(f.Name, _icons.Key(f.IsDirectory, f.Extension)) { Tag = f };
            item.SubItems.Add(f.IsDirectory ? "" : PhoneFiles.FormatSize(f.Size));
            item.SubItems.Add(f.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
            item.SubItems.Add(f.IsDirectory ? "Folder" : f.Extension.Length > 0 ? f.Extension.ToUpperInvariant() + " file" : "File");
            item.Selected = keep.Contains(f.Name);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateState();
    }

    private IEnumerable<PhoneFile> Sorted(IEnumerable<PhoneFile> files)
    {
        var folders = files.OrderByDescending(f => f.IsDirectory);
        IOrderedEnumerable<PhoneFile> by = _sortColumn switch
        {
            1 => _sortDescending ? folders.ThenByDescending(f => f.Size) : folders.ThenBy(f => f.Size),
            2 => _sortDescending ? folders.ThenByDescending(f => f.Modified) : folders.ThenBy(f => f.Modified),
            3 => _sortDescending ? folders.ThenByDescending(f => f.Extension) : folders.ThenBy(f => f.Extension),
            _ => _sortDescending ? folders.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase) : folders.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
        };
        return by;
    }

    private void SortBy(int column)
    {
        _sortDescending = column == _sortColumn && !_sortDescending;
        _sortColumn = column;
        Fill();
    }

    private IReadOnlyList<PhoneFile> Selected => _list.SelectedItems.Cast<ListViewItem>().Select(i => (PhoneFile)i.Tag!).ToList();

    // ---- Open, download, upload ---------------------------------------------------------------------------

    /// <summary>A folder opens in place; a file is downloaded to a temporary folder and opened with its PC app.</summary>
    private async Task OpenSelectedAsync()
    {
        if (_busy || Selected is not [var f]) return;
        if (f.IsDirectory)
        {
            await NavigateAsync(PhoneFiles.Combine(_folder, f.Name));
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), "Streamy", Sanitize(_serial));
        Directory.CreateDirectory(temp);
        SetBusy(true, $"Opening {f.Name}");
        try
        {
            await AdbClient.Default.PullAsync(_serial, PhoneFiles.Combine(_folder, f.Name), temp);
            Process.Start(new ProcessStartInfo(Path.Combine(temp, f.Name)) { UseShellExecute = true });
            Report($"Opened {f.Name}");
        }
        catch (Exception ex)
        {
            Report($"Could not open {f.Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DownloadSelectedAsync()
    {
        var files = Selected;
        if (files.Count == 0) return;
        using var dlg = new FolderBrowserDialog
        {
            Description = "Download to",
            UseDescriptionForTitle = true,
            SelectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(files.Select(f => (f.Name, (Func<Task>)(() => AdbClient.Default.PullAsync(_serial, PhoneFiles.Combine(_folder, f.Name), dlg.SelectedPath)))).ToList(),
            "Downloading", n => $"Downloaded {Items(n)} to {SaveFolders.Short(dlg.SelectedPath)}", reload: false);
    }

    private async Task PickAndUploadAsync()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = $"Upload to {_folder}" };
        if (dlg.ShowDialog(this) == DialogResult.OK) await UploadAsync(dlg.FileNames);
    }

    /// <summary>Copies PC files and folders into the open phone folder. Existing names are replaced, as adb push does.</summary>
    private async Task UploadAsync(IReadOnlyList<string> paths)
    {
        if (_busy) return;
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (items.Count == 0) return;
        var clashes = items.Select(p => Path.GetFileName(p.TrimEnd('\\'))).Where(n => _files.Any(f => f.Name.Equals(n, StringComparison.Ordinal))).ToList();
        if (clashes.Count > 0 && MessageBox.Show(this, $"Replace {string.Join(", ", clashes.Take(5))}{(clashes.Count > 5 ? " and others" : "")} on the phone?",
                "Streamy", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await RunAsync(items.Select(p => (Path.GetFileName(p.TrimEnd('\\')), (Func<Task>)(() => AdbClient.Default.PushIntoAsync(_serial, p, _folder)))).ToList(),
            "Uploading", n => $"Uploaded {Items(n)}", reload: true);
    }

    // ---- Create, rename, delete ---------------------------------------------------------------------------

    private async Task NewFolderAsync()
    {
        var name = PhoneFiles.UniqueName("New folder", _files.Select(f => f.Name));
        var input = Prompt.Show(this, "New folder", $"Name of the new folder in {_folder}:", initial: name);
        if (input is null) return;
        if (!PhoneFiles.IsValidName(input)) { Report("That is not a valid folder name."); return; }
        await RunAsync([(input, () => AdbClient.Default.MakeFolderAsync(_serial, PhoneFiles.Combine(_folder, input)))], "Creating", _ => $"Created {input}", reload: true);
    }

    private void BeginRename()
    {
        if (_list.SelectedItems.Count == 1) _list.SelectedItems[0].BeginEdit();
    }

    private async Task RenameAsync(LabelEditEventArgs e)
    {
        e.CancelEdit = true; // the list is refilled from the phone either way
        if (e.Label is null) return;
        var file = (PhoneFile)_list.Items[e.Item].Tag!;
        var name = e.Label.Trim();
        if (name == file.Name) return;
        if (!PhoneFiles.IsValidName(name)) { Report("That is not a valid name."); return; }
        await RunAsync([(file.Name, () => AdbClient.Default.RenameAsync(_serial, PhoneFiles.Combine(_folder, file.Name), PhoneFiles.Combine(_folder, name)))],
            "Renaming", _ => $"Renamed {file.Name} to {name}", reload: true);
    }

    private async Task DeleteSelectedAsync()
    {
        var files = Selected;
        if (files.Count == 0 || _busy) return;
        string what = files.Count == 1 ? $"\"{files[0].Name}\"" + (files[0].IsDirectory ? " and everything in it" : "") : $"these {files.Count} items";
        if (MessageBox.Show(this, $"Delete {what} from the phone? This cannot be undone.", "Streamy",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunAsync([(Items(files.Count), () => AdbClient.Default.DeleteAsync(_serial, files.Select(f => PhoneFiles.Combine(_folder, f.Name))))],
            "Deleting", _ => $"Deleted {Items(files.Count)}", reload: true);
    }

    // ---- Plumbing -----------------------------------------------------------------------------------------

    /// <summary>Runs steps one by one with a status line, collects failures, then reloads the folder if asked.</summary>
    private async Task RunAsync(IReadOnlyList<(string Name, Func<Task> Step)> steps, string verb, Func<int, string> done, bool reload)
    {
        if (_busy) return;
        int ok = 0;
        var failures = new List<string>();
        SetBusy(true, "");
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var (name, step) = steps[i];
                _status.Text = $"{verb} {name}" + (steps.Count > 1 ? $" ({i + 1}/{steps.Count})" : "");
                try
                {
                    await step();
                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"{verb} {name}");
                    failures.Add($"{name}: {ex.Message}");
                }
            }
        }
        finally
        {
            SetBusy(false);
        }
        if (reload) await LoadAsync(_folder);
        Report(failures.Count == 0 ? done(ok) : $"{ok} done, {failures.Count} failed");
        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n", failures), "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        if (text is not null) _status.Text = text;
        UseWaitCursor = busy;
        UpdateState();
    }

    private string? _report;

    private void Report(string text)
    {
        _report = text;
        _status.Text = text;
    }

    private void UpdateState()
    {
        var sel = Selected;
        foreach (var b in _actions) b.Enabled = !_busy;
        _up.Enabled = !_busy && PhoneFiles.Parent(_folder) is not null;
        _download.Enabled = !_busy && sel.Count > 0;
        _rename.Enabled = !_busy && sel.Count == 1;
        _delete.Enabled = !_busy && sel.Count > 0;
        if (_busy) return;
        long bytes = sel.Where(f => !f.IsDirectory).Sum(f => f.Size);
        string counts = sel.Count > 0
            ? $"{Items(_files.Count)}, {sel.Count} selected" + (bytes > 0 ? $" ({PhoneFiles.FormatSize(bytes)})" : "")
            : Items(_files.Count) + ". Drop files here to upload them.";
        _status.Text = _report is null ? counts : $"{_report}. {counts}";
        _report = null;
    }

    private static string Items(int n) => n == 1 ? "1 item" : $"{n} items";

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private Button Action(string text, Action onClick, Control parent, string glyph)
    {
        var b = new Button { Text = text, AutoSize = true };
        Glyph.Apply(b, glyph);
        b.Click += (_, _) => onClick();
        parent.Controls.Add(b);
        _actions.Add(b);
        return b;
    }

    private Button Action(string text, Func<Task> onClick, Control parent, string glyph) => Action(text, () => { _ = onClick(); }, parent, glyph);

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Open", null, async (_, _) => await OpenSelectedAsync());
        var download = menu.Items.Add("Download", null, async (_, _) => await DownloadSelectedAsync());
        var rename = menu.Items.Add("Rename", null, (_, _) => BeginRename());
        var delete = menu.Items.Add("Delete", null, async (_, _) => await DeleteSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("New folder", null, async (_, _) => await NewFolderAsync());
        menu.Items.Add("Upload", null, async (_, _) => await PickAndUploadAsync());
        menu.Items.Add("Refresh", null, async (_, _) => await LoadAsync(_folder));
        menu.Opening += (_, e) =>
        {
            var sel = Selected;
            open.Enabled = sel.Count == 1;
            download.Enabled = delete.Enabled = sel.Count > 0;
            rename.Enabled = sel.Count == 1;
            if (_busy) e.Cancel = true;
        };
        return menu;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_path.Focused || _busy || _editing) return; // typing a path or a new name
        switch (e.KeyCode)
        {
            case Keys.Back: e.Handled = true; await GoUpAsync(); break;
            case Keys.Delete: e.Handled = true; await DeleteSelectedAsync(); break;
            case Keys.F2: e.Handled = true; BeginRename(); break;
            case Keys.F5: e.Handled = true; await LoadAsync(_folder); break;
            case Keys.A when e.Control:
                e.Handled = true;
                foreach (ListViewItem i in _list.Items) i.Selected = true;
                break;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _icons.Dispose();
        base.OnFormClosed(e);
    }
}
