using Streamy.Core.Geometry;
using Streamy.Core.Input;
using Streamy.Core.Protocol;
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
    private long _lastPresented;
    private Size _frameSize;
    private char _highSurrogate;
    private ulong _clipboardSequence;

    public SessionWindow(MirrorSession session)
    {
        _session = session;
        Text = $"{session.DeviceName} - Streamy";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = false;
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
    }

    // ---- Toolbar ------------------------------------------------------------------------------------------

    private void BuildToolbar()
    {
        _toolbar.Add("", "Power (right-click: turn the phone's screen off, keep mirroring)", _tips,
            () => PressKey(AndroidKeycode.Power), () => _session.Send(ControlMessage.SetDisplayPower(false)));
        _toolbar.Add("", "Volume up", _tips, () => PressKey(AndroidKeycode.VolumeUp));
        _toolbar.Add("", "Volume down", _tips, () => PressKey(AndroidKeycode.VolumeDown));
        _toolbar.Add("", "Rotate", _tips, () => _session.Send(ControlMessage.RotateDevice()));
        _toolbar.Add("", "Back (or right-click the screen)", _tips, () => PressKey(AndroidKeycode.Back));
        _toolbar.Add("", "Home (or middle-click the screen)", _tips, () => PressKey(AndroidKeycode.Home));
        _toolbar.Add("", "Recent apps", _tips, () => PressKey(AndroidKeycode.AppSwitch));
        _toolbar.Add("", "Notifications (right-click: quick settings)", _tips,
            () => _session.Send(ControlMessage.ExpandNotificationPanel()), () => _session.Send(ControlMessage.ExpandSettingsPanel()));
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
        if (text.Length == 1 && char.IsControl(text[0])) return; // Enter, Tab, Backspace... went as key events
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
        if (_renderer is null) return;
        long presented = _renderer.PresentedFrames;
        Text = $"{_session.DeviceName} - {presented - _lastPresented} fps - Streamy";
        _lastPresented = presented;
    }

    /// <summary>
    /// Sizes the window to the phone's aspect ratio (plus the toolbar): on start, to 85% of the screen's working
    /// height (or 70% of its width for a landscape phone); on rotation, keeping the picture's current longer side.
    /// </summary>
    private void FitTo(Size frame, bool center)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;
        if (frame == _frameSize) { UpdateMapper(); return; }
        var area = Screen.FromControl(this).WorkingArea;
        var chrome = Size - ClientSize + new Size(ToolBar.BarWidth, 0);

        int longSide = center || _frameSize.IsEmpty
            ? (int)(frame.Height >= frame.Width ? area.Height * 0.85 : area.Width * 0.7)
            : Math.Max(_view.ClientSize.Width, _view.ClientSize.Height);
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

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _tips.Dispose();
        base.OnFormClosed(e);
        if (_session.Decoder is { } decoder && _renderer is not null) decoder.FrameDecoded -= _renderer.Signal;
        _renderer?.Dispose();
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
