using Streamy.Core.Geometry;
using Streamy.Core.Input;
using Streamy.Core.Protocol;
using Streamy.Audio;
using Streamy.Services;
using Streamy.Session;
using Streamy.Video;

namespace Streamy.UI;

/// <summary>
/// One window per mirrored phone: its screen, controlled with the mouse and keyboard, and a toolbar on the right.
/// </summary>
internal sealed class SessionWindow : Form
{
    private readonly MirrorSession _session;
    private readonly VideoView _view = new() { Dock = DockStyle.Fill };
    private readonly ToolBar _toolbar = new();
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly InputMapper _input = new();
    private D3DRenderer? _renderer;
    private AudioPlayer? _player;
    private ToolButton? _recordButton, _muteButton;
    private long _lastPresented;
    private Size _frameSize;
    private char _highSurrogate;
    private ulong _clipboardSequence;

    public SessionWindow(MirrorSession session)
    {
        _session = session;
        Text = $"{session.DeviceName} - Streamy";
        Icon = AppIcon.Load();
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = false;
        TopMost = AppSettings.Current.AlwaysOnTop;
        AllowDrop = true;
        _view.AllowDrop = true;
        _view.DragEnter += OnDragEnter;
        _view.DragDrop += OnDragDrop;
        Controls.Add(_view);
        Controls.Add(_toolbar);
        BuildToolbar();
        FitTo(session.Width > 0 ? new Size(session.Width, session.Height) : new Size(1080, 2340), center: true);

        _session.SizeChanged += (w, h) => BeginInvoke(() => FitTo(new Size(w, h), center: false));
        _session.DeviceMessageReceived += OnDeviceMessage;
        _session.Ended += ex => BeginInvoke(() =>
        {
            Text = $"{_session.DeviceName} - disconnected";
            if (ex is not null && !IsDisposed) MessageBox.Show(this, ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        _view.Resize += (_, _) =>
        {
            UpdateMapper();
            _renderer?.Resize(_view.ClientSize.Width, _view.ClientSize.Height);
        };
        HookInput();
        _timer.Tick += (_, _) => UpdateTitle();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _view.Focus();
        if (_session.Decoder is not { } decoder) return;
        _renderer = new D3DRenderer(_view.Handle, decoder.Frames, _view.ClientSize.Width, _view.ClientSize.Height);
        _renderer.Failed += ex => BeginInvoke(() => MessageBox.Show(this, "Drawing failed: " + ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Error));
        decoder.FrameDecoded += _renderer.Signal;
        _renderer.Signal();
        _timer.Start();
        StartAudio();
        if (AppSettings.Current.TurnScreenOff) _session.Send(ControlMessage.SetDisplayPower(false));
    }

    // ---- Files --------------------------------------------------------------------------------------------

    private bool _sending;

    private void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true && !_sending ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>APKs are installed (or updated); anything else is copied to the phone's Download folder.</summary>
    private async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || _sending) return;
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return;
        _sending = true;
        FileSender.Result result;
        try
        {
            result = await FileSender.SendAsync(_session.Serial, files,
                progress: text => Flash(text, seconds: 600),
                scan: path => _session.Send(ControlMessage.ScanFile(path))); // show it in Gallery / Files at once
        }
        finally
        {
            _sending = false;
        }
        Flash(result.Summary);
        if (result.Failures.Count > 0)
            MessageBox.Show(this, string.Join("\n", result.Failures), "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---- Fullscreen ---------------------------------------------------------------------------------------

    private FormWindowState _windowedState;
    private Rectangle _windowedBounds;

    /// <summary>F11: the phone's screen fills the monitor, without the title bar and toolbar.</summary>
    private void ToggleFullscreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            _toolbar.Visible = true;
            WindowState = _windowedState;
            if (_windowedState == FormWindowState.Normal) Bounds = _windowedBounds;
            return;
        }
        _windowedState = WindowState;
        _windowedBounds = Bounds;
        _toolbar.Visible = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        Bounds = Screen.FromControl(this).Bounds;
    }

    // ---- Audio, screenshots, recording --------------------------------------------------------------------

    private async void StartAudio()
    {
        if (_session.Audio is not { } audio) return;
        try
        {
            _player = await AudioPlayer.CreateAsync();
            _player.Muted = AppSettings.Current.Muted;
            _player.Volume = AppSettings.Current.Volume / 100f;
            if (IsDisposed) { _player.Dispose(); _player = null; return; }
            audio.Samples += _player.Write;
            UpdateMuteButton();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "audio output");
            _player = null; // no usable audio output; mirroring goes on without sound
        }
    }

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.Muted = !_player.Muted;
        AppSettings.Current.Muted = _player.Muted;
        AppSettings.Current.Save();
        UpdateMuteButton();
    }

    private void UpdateMuteButton()
    {
        if (_muteButton is null) return;
        _muteButton.Text = _player is { Muted: true } ? Glyph.Muted : Glyph.Speaker;
        _muteButton.Invalidate();
    }

    private void TakeScreenshot()
    {
        if (_session.Decoder?.Frames.CopyLatest() is not { } frame) return;
        var dir = SaveFolders.Screenshots;
        var file = Path.Combine(dir, $"{FileSafe(_session.DeviceName)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        using var bmp = FrameSnapshot.ToBitmap(frame);
        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        try { Clipboard.SetImage(bmp); } catch (System.Runtime.InteropServices.ExternalException) { }
        Flash($"Screenshot saved to {SaveFolders.Short(dir)} and copied");
    }

    private void ToggleRecording()
    {
        if (_session.Recorder is not null)
        {
            var rec = _session.StopRecording()!;
            _recordButton!.ForeColor = Color.FromArgb(0xE6, 0xE6, 0xE6);
            _recordButton.Invalidate();
            Flash(rec.HasStarted ? $"Recording saved: {Path.GetFileName(rec.Path)}" : "Recording stopped before the first frame");
            return;
        }
        var dir = SaveFolders.Recordings;
        var file = Path.Combine(dir, $"{FileSafe(_session.DeviceName)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        _session.StartRecording(file);
        _recordButton!.ForeColor = Color.FromArgb(0xE8, 0x3B, 0x3B);
        _recordButton.Invalidate();
        Flash($"Recording to {SaveFolders.Short(dir)}");
    }

    private static string FileSafe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "Phone" : name;
    }

    private string? _flash;
    private DateTime _flashUntil;

    /// <summary>Shows a short message in the title bar for a few seconds.</summary>
    private void Flash(string message, int seconds = 4)
    {
        _flash = message;
        _flashUntil = DateTime.Now.AddSeconds(seconds);
        UpdateTitle();
    }

    // ---- Toolbar ------------------------------------------------------------------------------------------

    private void BuildToolbar()
    {
        _toolbar.Add(Glyph.Power, "Power (right-click: turn the phone's screen off, keep mirroring)", _tips,
            () => PressKey(AndroidKeycode.Power), () => _session.Send(ControlMessage.SetDisplayPower(false)));
        _toolbar.Add(Glyph.VolumeUp, "Volume up", _tips, () => PressKey(AndroidKeycode.VolumeUp));
        _toolbar.Add(Glyph.VolumeDown, "Volume down", _tips, () => PressKey(AndroidKeycode.VolumeDown));
        _toolbar.Add(Glyph.Rotate, "Rotate", _tips, () => _session.Send(ControlMessage.RotateDevice()));
        _toolbar.Add(Glyph.Back, "Back (or right-click the screen)", _tips, () => PressKey(AndroidKeycode.Back));
        _toolbar.Add(Glyph.Home, "Home (or middle-click the screen)", _tips, () => PressKey(AndroidKeycode.Home));
        _toolbar.Add(Glyph.Recents, "Recent apps", _tips, () => PressKey(AndroidKeycode.AppSwitch));
        _toolbar.Add(Glyph.Notifications, "Notifications (right-click: quick settings)", _tips,
            () => _session.Send(ControlMessage.ExpandNotificationPanel()), () => _session.Send(ControlMessage.ExpandSettingsPanel()));
        _toolbar.AddSeparator();
        _toolbar.Add(Glyph.Folder, "Browse the phone's files", _tips, () => FileManager.Open(_session.Serial, _session.DeviceName));
        _toolbar.Add(Glyph.Gallery, "Photos and videos", _tips, () => FileManager.OpenGallery(_session.Serial, _session.DeviceName));
        _toolbar.Add(Glyph.Camera, "Screenshot (saved and copied to the clipboard)", _tips, TakeScreenshot);
        _recordButton = _toolbar.Add(Glyph.Record, "Record to MP4 (click again to stop)", _tips, ToggleRecording);
        _muteButton = _toolbar.Add(Glyph.Speaker, "Mute the phone's sound on the PC", _tips, ToggleMute);
        _toolbar.AddSeparator();
        _toolbar.Add(Glyph.Fullscreen, "Fullscreen (F11 to leave)", _tips, ToggleFullscreen);
        _toolbar.Add(Glyph.Pin, "Keep this window on top (right-click: always, for every phone)", _tips,
            () => TopMost = !TopMost,
            () => { AppSettings.Current.AlwaysOnTop = TopMost = !AppSettings.Current.AlwaysOnTop; AppSettings.Current.Save(); });
    }

    private void PressKey(AndroidKeycode key)
    {
        _session.Send(ControlMessage.InjectKeycode(AndroidKeyAction.Down, key, 0, AndroidMetaState.None));
        _session.Send(ControlMessage.InjectKeycode(AndroidKeyAction.Up, key, 0, AndroidMetaState.None));
        _view.Focus();
    }

    // ---- Mouse and keyboard -------------------------------------------------------------------------------

    private void HookInput()
    {
        _view.MouseDown += (_, e) =>
        {
            _view.Focus();
            if (ToButton(e.Button) is not { } b) return;
            if (b == MouseButton.Left && !_input.View.Contains(e.X, e.Y)) return; // a click in the black bars
            _session.Send(_input.MouseDown(b, e.X, e.Y, Modifiers()));
        };
        _view.MouseMove += (_, e) => _session.Send(_input.MouseMove(e.X, e.Y));
        _view.MouseUp += (_, e) =>
        {
            if (ToButton(e.Button) is { } b) _session.Send(_input.MouseUp(b, e.X, e.Y));
        };
        _view.MouseCaptureChanged += (_, _) =>
        {
            // Another window took the mouse mid-drag: lift the finger where it is.
            if (_input.IsDragging && !_view.Capture)
            {
                var p = _view.PointToClient(Cursor.Position);
                _session.Send(_input.CancelDrag(p.X, p.Y));
            }
        };
        _view.MouseWheel += (_, e) => _session.Send(_input.Wheel(e.X, e.Y, e.Delta, horizontal: false));
        _view.HorizontalWheel += (x, y, delta) => _session.Send(_input.Wheel(x, y, delta, horizontal: true));

        _view.KeyDown += OnKeyDown;
        _view.KeyUp += (_, e) =>
        {
            _repeats.Remove(e.KeyValue);
            if (_input.KeyUp(e.KeyValue, Modifiers()) is { } msg) _session.Send(msg);
        };
        _view.KeyPress += OnKeyPress;
        _view.LostFocus += (_, _) =>
        {
            _repeats.Clear();
            _session.Send(_input.ReleaseAllKeys());
        };
    }

    private readonly Dictionary<int, uint> _repeats = [];

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = Modifiers();
        // F11 is Streamy's own: fullscreen on and off. Phones have no use for it.
        if (e.KeyCode == Keys.F11 && mods == KeyModifiers.None)
        {
            ToggleFullscreen();
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        // Alt+F4 closes the window, as everywhere in Windows; it is not sent to the phone.
        if (e.KeyCode == Keys.F4 && mods == KeyModifiers.Alt)
        {
            e.Handled = e.SuppressKeyPress = true;
            Close();
            return;
        }
        // Ctrl+V or Shift+Insert: put the PC's clipboard on the phone and paste it.
        if ((e.KeyCode == Keys.V && mods == KeyModifiers.Ctrl) || (e.KeyCode == Keys.Insert && mods == KeyModifiers.Shift))
        {
            PasteFromPc();
            e.Handled = e.SuppressKeyPress = true;
            return;
        }

        uint repeat = _repeats.TryGetValue(e.KeyValue, out var r) ? r + 1 : 0;
        _repeats[e.KeyValue] = repeat;
        if (_input.KeyDown(e.KeyValue, mods, repeat) is { } msg)
        {
            _session.Send(msg);
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    private void OnKeyPress(object? sender, KeyPressEventArgs e)
    {
        char c = e.KeyChar;
        e.Handled = true;
        if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
        string text = char.IsLowSurrogate(c) && _highSurrogate != 0 ? new string([_highSurrogate, c]) : c.ToString();
        _highSurrogate = '\0';
        if (text.Length == 1 && char.IsControl(text[0])) return; // Enter, Tab and Backspace went as key events
        if (TextInjection.NeedsPaste(text))
        {
            // The phone can only type characters its built-in key map knows (ASCII); scrcpy drops the rest.
            // Arabic, accents and emoji go through the phone's clipboard and are pasted instead.
            _lastPhoneClipboard = text;
            _session.Send(ControlMessage.SetClipboard(++_clipboardSequence, text, paste: true));
            return;
        }
        _session.Send(ControlMessage.InjectText(text));
    }

    private static KeyModifiers Modifiers()
    {
        var k = Control.ModifierKeys;
        var m = KeyModifiers.None;
        if ((k & Keys.Shift) != 0) m |= KeyModifiers.Shift;
        if ((k & Keys.Control) != 0) m |= KeyModifiers.Ctrl;
        if ((k & Keys.Alt) != 0) m |= KeyModifiers.Alt;
        return m;
    }

    private static MouseButton? ToButton(MouseButtons b) => b switch
    {
        MouseButtons.Left => MouseButton.Left,
        MouseButtons.Right => MouseButton.Right,
        MouseButtons.Middle => MouseButton.Middle,
        MouseButtons.XButton1 => MouseButton.Back,
        MouseButtons.XButton2 => MouseButton.Forward,
        _ => null,
    };

    // ---- Clipboard ----------------------------------------------------------------------------------------

    private void PasteFromPc()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { return; } // clipboard busy
        if (text.Length == 0) return;
        _lastPhoneClipboard = text; // the phone will echo it back; don't treat that as news
        _session.Send(ControlMessage.SetClipboard(++_clipboardSequence, text, paste: true));
    }

    private string? _lastPhoneClipboard;

    private void OnDeviceMessage(DeviceMessage msg)
    {
        if (msg is not DeviceMessage.Clipboard c || c.Text.Length == 0) return;
        BeginInvoke(() =>
        {
            if (c.Text == _lastPhoneClipboard) return;
            _lastPhoneClipboard = c.Text;
            try { Clipboard.SetText(c.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { } // clipboard busy; the next copy will sync
        });
    }

    // ---- Layout -------------------------------------------------------------------------------------------

    private void UpdateMapper()
    {
        var frame = _session.Width > 0 ? new Size(_session.Width, _session.Height) : _frameSize;
        _input.View = new ViewMapper(_view.ClientSize.Width, _view.ClientSize.Height, frame.Width, frame.Height);
    }

    private void UpdateTitle()
    {
        long presented = _renderer?.PresentedFrames ?? 0;
        string rec = _session.Recorder is { } r ? $" - REC {DateTime.Now - r.StartedAt:mm\\:ss}" : "";
        string flash = _flash is not null && DateTime.Now < _flashUntil ? " - " + _flash : "";
        Text = $"{_session.DeviceName} - {presented - _lastPresented} fps{rec}{flash} - Streamy";
        _lastPresented = presented;
    }

    /// <summary>
    /// Sizes the window to the phone's aspect ratio (plus the toolbar): on start, to 85% of the screen's working
    /// height (or 70% of its width for a landscape phone); on rotation, keeping the picture's current longer side.
    /// </summary>
    private void FitTo(Size frame, bool center)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;
        if (FormBorderStyle == FormBorderStyle.None) { _frameSize = frame; UpdateMapper(); return; } // fullscreen: the picture letterboxes itself
        if (frame == _frameSize) { UpdateMapper(); return; }
        var area = Screen.FromControl(this).WorkingArea;
        var chrome = Size - ClientSize + new Size(ToolBar.BarWidth, 0);

        // The picture's long side is what the user chose (by resizing) or the first fit; a rotation keeps it, even
        // when the screen made the previous orientation smaller, so rotating back and forth never shrinks the window.
        if (center || _preferredLongSide == 0)
            _preferredLongSide = (int)(frame.Height >= frame.Width ? area.Height * 0.85 : area.Width * 0.7);
        int longSide = _preferredLongSide;
        _frameSize = frame;
        double scale = (double)longSide / Math.Max(frame.Width, frame.Height);
        var video = new Size((int)(frame.Width * scale), (int)(frame.Height * scale));

        // Never larger than the screen.
        double shrink = Math.Min(1, Math.Min((double)(area.Width - chrome.Width) / video.Width, (double)(area.Height - chrome.Height) / video.Height));
        video = new Size((int)(video.Width * shrink), (int)(video.Height * shrink));

        var old = Bounds;
        ClientSize = new Size(video.Width + ToolBar.BarWidth, video.Height);
        if (center)
        {
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
        else
        {
            // Rotate around the window's centre, kept on screen.
            int x = old.Left + (old.Width - Width) / 2, y = old.Top + (old.Height - Height) / 2;
            Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
        UpdateMapper();
    }

    private int _preferredLongSide;

    /// <summary>The user resized the window by hand: remember the size for the next rotation.</summary>
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (FormBorderStyle != FormBorderStyle.None)
            _preferredLongSide = Math.Max(_view.ClientSize.Width, _view.ClientSize.Height);
    }

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _tips.Dispose();
        base.OnFormClosed(e);
        if (_session.Decoder is { } decoder && _renderer is not null) decoder.FrameDecoded -= _renderer.Signal;
        _renderer?.Dispose();
        if (_session.Audio is { } audio && _player is not null) audio.Samples -= _player.Write;
        _player?.Dispose();
        await _session.DisposeAsync();
    }

    /// <summary>
    /// The child window Direct3D draws into. WinForms never paints it. It takes keyboard focus and receives
    /// every key (arrows, Tab, Enter) instead of letting the form use them for navigation.
    /// </summary>
    private sealed class VideoView : Control
    {
        private const int WM_MOUSEHWHEEL = 0x020E;

        /// <summary>Tilt wheel / touchpad horizontal scroll: x, y (client), delta.</summary>
        public event Action<int, int, int>? HorizontalWheel;

        public VideoView()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            TabStop = true;
        }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override bool ProcessDialogKey(Keys keyData) => false;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEHWHEEL)
            {
                int delta = (short)((long)m.WParam >> 16);
                var p = PointToClient(new Point((short)(long)m.LParam, (short)((long)m.LParam >> 16)));
                HorizontalWheel?.Invoke(p.X, p.Y, delta);
                m.Result = 1;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
