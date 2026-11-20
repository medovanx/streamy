using System.Diagnostics;
using System.Drawing.Drawing2D;
using Streamy.Core.Files;
using Streamy.Core.Media;
using Streamy.Services;

namespace Streamy.UI;

/// <summary>
/// Full-window photo and video viewer inside Streamy. Swipe with the mouse (drag sideways and let go), the wheel,
/// the arrow keys or the buttons, like on the phone. Photos show the phone's thumbnail at once and sharpen when the
/// full picture arrives; the next and previous ones are prepared in the background. Videos play in place.
/// </summary>
internal sealed class MediaViewer : Form
{
    private readonly string _serial;
    private readonly List<MediaItem> _items; // the viewer's own copy: deleting here does not touch the gallery's list
    private readonly ThumbnailLoader _thumbs;
    private readonly LocationIndex _locations;
    private readonly PhotoView _photo = new() { Dock = DockStyle.Fill };
    private readonly Panel _stage = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Panel _bar = new() { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(0x1A, 0x1A, 0x1A) };
    private readonly Label _info = new() { Dock = DockStyle.Fill, ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };
    private readonly Dictionary<long, Bitmap> _full = []; // decoded photos near the current one
    private readonly Dictionary<long, Task> _preparing = [];
    private readonly Dictionary<long, MediaDetails> _details = [];
    private readonly VideoPanel _video = new() { Dock = DockStyle.Fill, Visible = false };
    private int _index;
    private CancellationTokenSource _loadCts = new();
    private FormWindowState _windowedState;
    private bool _fullscreen;

    /// <summary>Raised after items were deleted from the viewer, so the gallery can refresh.</summary>
    public event Action? Deleted;

    public MediaViewer(string serial, string deviceName, IReadOnlyList<MediaItem> items, int start, ThumbnailLoader thumbs, LocationIndex locations)
    {
        _locations = locations;
        _serial = serial;
        _items = [.. items];
        _thumbs = thumbs;
        _index = Math.Clamp(start, 0, items.Count - 1);
        Text = $"{deviceName} - Streamy";
        Icon = AppIcon.Load();
        BackColor = Color.Black;
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Size = new Size((int)(area.Width * 0.8), (int)(area.Height * 0.88));

        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(8, 8, 0, 0), WrapContents = false };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(0, 8, 8, 0), WrapContents = false };
        left.Controls.AddRange([Btn("Previous", Glyph.Back, () => Go(-1)), Btn("Next", Glyph.Forward, () => Go(1))]);
        right.Controls.AddRange([
            Btn("Download", Glyph.Download, async () => await DownloadAsync()),
            Btn("Open in app", Glyph.FolderOpen, async () => await OpenInAppAsync()),
            Btn("Delete", Glyph.Delete, async () => await DeleteAsync()),
            Btn("Fullscreen", Glyph.Fullscreen, ToggleFullscreen),
        ]);
        _bar.Controls.Add(_info);
        _bar.Controls.Add(left);
        _bar.Controls.Add(right);

        _photo.Swiped += Go;
        _photo.MouseWheel += (_, e) => Go(e.Delta > 0 ? -1 : 1);
        _photo.DoubleClick += (_, _) => ToggleFullscreen();
        _photo.NeighbourImage = d => Picture(_index + d);
        _stage.Controls.Add(_photo);
        _stage.Controls.Add(_video);
        _video.Swiped += Go;
        _video.Failed += message =>
        {
            _video.Visible = false;
            _photo.Visible = true;
            _photo.Message = message + ". Use Open in app.";
        };
        Controls.Add(_stage);
        Controls.Add(_bar);
        _thumbs.Loaded += ThumbArrived;
        Shown += (_, _) => Show(_index);
    }

    private MediaItem Current => _items[_index];

    // ---- Showing an item ----------------------------------------------------------------------------------

    private void Go(int step)
    {
        int next = Math.Clamp(_index + step, 0, _items.Count - 1);
        if (next != _index) Show(next);
        else _photo.SnapBack();
    }

    private void Show(int index)
    {
        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        _index = index;
        var item = Current;
        StopVideo();
        _photo.Visible = true;
        _photo.Image = Picture(index);
        _photo.Message = null;
        UpdateInfo();
        if (item.IsVideo) _ = PlayAsync(item, _loadCts.Token);
        else _ = SharpenAsync(item, _loadCts.Token);
        _ = DetailsAsync(item);
        Prepare(index + 1);
        Prepare(index - 1);
        Forget(index);
    }

    /// <summary>The best picture we have for an index: the full photo if ready, else the phone's thumbnail.</summary>
    private Image? Picture(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        var item = _items[index];
        return _full.TryGetValue(item.Id, out var full) ? full : _thumbs.Get(item);
    }

    private async Task SharpenAsync(MediaItem item, CancellationToken ct)
    {
        try
        {
            if (!_full.ContainsKey(item.Id))
            {
                if (_photo.Image is null) _photo.Message = "Loading";
                await PrepareAsync(item);
            }
            if (ct.IsCancellationRequested) return;
            _photo.Message = null;
            if (_full.TryGetValue(item.Id, out var bmp)) _photo.Image = bmp;
        }
        catch (NotSupportedException)
        {
            if (!ct.IsCancellationRequested)
                _photo.Message = _photo.Image is null ? "Windows cannot open this kind of picture" : null;
            _info.Text += "  ·  Showing the phone's preview: Windows needs the HEIF extension from the Microsoft Store for full size";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "viewer photo");
            if (!ct.IsCancellationRequested) _photo.Message = "Could not load: " + ex.Message;
        }
    }

    /// <summary>Copies and decodes a photo into <see cref="_full"/>; one task per photo, shared by everyone waiting.</summary>
    private Task PrepareAsync(MediaItem item)
    {
        if (_preparing.TryGetValue(item.Id, out var running)) return running;
        int maxSide = Math.Max(Screen.FromControl(this).Bounds.Width, Screen.FromControl(this).Bounds.Height);
        var task = Task.Run(async () =>
        {
            var path = await MediaCache.GetAsync(_serial, item);
            return await PhotoDecoder.DecodeAsync(path, maxSide);
        }).ContinueWith(t =>
        {
            _preparing.Remove(item.Id);
            if (t.IsCompletedSuccessfully && !IsDisposed) _full[item.Id] = t.Result;
            if (t.Exception?.InnerException is { } ex) throw ex;
        }, TaskScheduler.FromCurrentSynchronizationContext());
        _preparing[item.Id] = task;
        return task;
    }

    private void Prepare(int index)
    {
        if (index < 0 || index >= _items.Count || _items[index].IsVideo || _full.ContainsKey(_items[index].Id)) return;
        _ = PrepareAsync(_items[index]).ContinueWith(_ => { }, TaskScheduler.Default); // errors show when the user gets there
    }

    /// <summary>Keeps decoded photos for the current item and two either side; frees the rest.</summary>
    private void Forget(int index)
    {
        var keep = Enumerable.Range(index - 2, 5).Where(i => i >= 0 && i < _items.Count).Select(i => _items[i].Id).ToHashSet();
        foreach (var id in _full.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            var bmp = _full[id];
            _full.Remove(id);
            if (!ReferenceEquals(_photo.Image, bmp)) bmp.Dispose();
        }
    }

    private void ThumbArrived()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (_photo.Image is null && !Current.IsVideo) _photo.Image = Picture(_index);
            _photo.Invalidate();
        });
    }

    // ---- Video --------------------------------------------------------------------------------------------

    private async Task PlayAsync(MediaItem item, CancellationToken ct)
    {
        try
        {
            string? path = MediaCache.Cached(_serial, item);
            if (path is null)
            {
                _photo.Message = $"Loading video, 0 of {PhoneFiles.FormatSize(item.Size)}";
                path = await MediaCache.GetAsync(_serial, item, new Progress<long>(b =>
                {
                    if (!ct.IsCancellationRequested) _photo.Message = $"Loading video, {PhoneFiles.FormatSize(b)} of {PhoneFiles.FormatSize(item.Size)}";
                }), ct);
            }
            if (ct.IsCancellationRequested) return;
            _photo.Message = null;
            _photo.Visible = false;
            _video.Visible = true;
            _video.Play(path);
            _video.Focus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "viewer video");
            if (!ct.IsCancellationRequested) _photo.Message = "Could not play: " + ex.Message;
        }
    }

    private void StopVideo()
    {
        _video.Stop();
        _video.Visible = false;
    }

    // ---- Details and actions ------------------------------------------------------------------------------

    private void UpdateInfo()
    {
        var i = Current;
        var parts = new List<string>
        {
            $"{_index + 1:N0} of {_items.Count:N0}",
            i.Name,
            i.Taken.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm"),
            PhoneFiles.FormatSize(i.Size),
        };
        if (i.Width > 0) parts.Add($"{i.Width} × {i.Height}");
        if (i.IsVideo) parts.Add(i.DurationText);
        if (_details.TryGetValue(i.Id, out var d) && d.CameraSettings is { } cam) parts.Add(cam);
        if (_locations.Place(i) is { } place) parts.Add(place);
        _info.Text = string.Join("  ·  ", parts);
    }

    private async Task DetailsAsync(MediaItem item)
    {
        if (_details.ContainsKey(item.Id)) return;
        try
        {
            var r = await AdbClient.Default.ShellAsync(_serial, [MediaDetails.QueryCommand(item)]);
            _details[item.Id] = MediaDetails.Parse(r.Output);
            if (!IsDisposed && Current.Id == item.Id) UpdateInfo();
        }
        catch (Exception ex) { Log.Info("details: " + ex.Message); }
    }

    private async Task DownloadAsync()
    {
        var item = Current;
        using var dlg = new FolderBrowserDialog
        {
            Description = "Download to",
            UseDescriptionForTitle = true,
            SelectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var local = await MediaCache.GetAsync(_serial, item);
            var target = Path.Combine(dlg.SelectedPath, item.Name);
            File.Copy(local, target, overwrite: true);
            _info.Text = $"Saved {item.Name} to {SaveFolders.Short(dlg.SelectedPath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not download: " + ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OpenInAppAsync()
    {
        try
        {
            var local = await MediaCache.GetAsync(_serial, Current);
            StopVideo();
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open: " + ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task DeleteAsync()
    {
        var item = Current;
        if (MessageBox.Show(this, $"Delete \"{item.Name}\" from the phone? It is removed for good, not moved to the phone's trash.",
                "Streamy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            StopVideo();
            await AdbClient.Default.DeleteMediaAsync(_serial, [item]);
            Deleted?.Invoke();
            _items.RemoveAt(_index);
            if (_items.Count == 0) { Close(); return; }
            Show(Math.Min(_index, _items.Count - 1));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not delete: " + ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _windowedState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _windowedState;
            if (WindowState == FormWindowState.Normal) CenterToScreen();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Left or Keys.PageUp: Go(-1); return true;
            case Keys.Right or Keys.PageDown: Go(1); return true;
            case Keys.Home: Show(0); return true;
            case Keys.End: Show(_items.Count - 1); return true;
            case Keys.F11: ToggleFullscreen(); return true;
            case Keys.Delete: _ = DeleteAsync(); return true;
            case Keys.Space when _video.Visible: _video.TogglePause(); return true;
            case Keys.Escape:
                if (_fullscreen) ToggleFullscreen(); else Close();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Button Btn(string text, string glyph, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0x2B, 0x2B, 0x2B) };
        b.FlatAppearance.BorderColor = Color.FromArgb(0x44, 0x44, 0x44);
        Glyph.Apply(b, glyph);
        b.Click += (_, _) => onClick();
        b.TabStop = false;
        return b;
    }

    private Button Btn(string text, string glyph, Func<Task> onClick) => Btn(text, glyph, () => { _ = onClick(); });

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _loadCts.Cancel();
        _thumbs.Loaded -= ThumbArrived;
        StopVideo();
        _video.Dispose();
        foreach (var b in _full.Values) if (!ReferenceEquals(_photo.Image, b)) b.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Draws one photo fitted to the window on black. Dragging sideways moves it and brings the next or previous
    /// one in from the side; letting go past a fifth of the width swipes, otherwise it springs back.
    /// </summary>
    private sealed class PhotoView : Control
    {
        private Image? _image;
        private string? _message;
        private int? _dragFrom;
        private int _offset;
        private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
        private int _animTarget;
        private int _animSwipe; // -1 previous, 1 next, 0 spring back

        public event Action<int>? Swiped;
        public Func<int, Image?>? NeighbourImage { get; set; }

        public PhotoView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            Cursor = Cursors.Hand;
            _anim.Tick += (_, _) => Step();
        }

        public Image? Image
        {
            get => _image;
            set { _image = value; _offset = 0; Invalidate(); }
        }

        public string? Message
        {
            get => _message;
            set { _message = value; Invalidate(); }
        }

        public void SnapBack()
        {
            _animSwipe = 0;
            _animTarget = 0;
            _anim.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _anim.Stop(); _dragFrom = e.X - _offset; Capture = true; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragFrom is not { } from) return;
            _offset = e.X - from;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragFrom is null) return;
            _dragFrom = null;
            Capture = false;
            int threshold = Width / 5;
            // Dragging left reveals the next item; dragging right the previous one.
            if (_offset < -threshold) { _animSwipe = 1; _animTarget = -Width; }
            else if (_offset > threshold) { _animSwipe = -1; _animTarget = Width; }
            else { _animSwipe = 0; _animTarget = 0; }
            _anim.Start();
        }

        private void Step()
        {
            int delta = _animTarget - _offset;
            if (Math.Abs(delta) < 8)
            {
                _anim.Stop();
                _offset = 0;
                int swipe = _animSwipe;
                _animSwipe = 0;
                if (swipe != 0) Swiped?.Invoke(swipe);
                Invalidate();
                return;
            }
            _offset += (int)(delta * 0.35);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Draw(g, _image, _offset);
            if (_offset < 0) Draw(g, NeighbourImage?.Invoke(1), _offset + Width);
            else if (_offset > 0) Draw(g, NeighbourImage?.Invoke(-1), _offset - Width);
            if (_message is not null)
            {
                using var font = new Font("Segoe UI", 11f);
                var size = TextRenderer.MeasureText(_message, font);
                var box = new Rectangle((Width - size.Width) / 2 - 12, Height - size.Height - 40, size.Width + 24, size.Height + 12);
                using var back = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
                g.FillRectangle(back, box);
                TextRenderer.DrawText(g, _message, font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void Draw(Graphics g, Image? img, int dx)
        {
            if (img is null) return;
            float scale = Math.Min((float)Width / img.Width, (float)Height / img.Height);
            float w = img.Width * scale, h = img.Height * scale;
            g.DrawImage(img, (Width - w) / 2 + dx, (Height - h) / 2, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }
}

