using Streamy.Session;
using Streamy.Video;

namespace Streamy.UI;

/// <summary>One window per mirrored phone, showing its screen. Input arrives in 0.3.0.</summary>
internal sealed class SessionWindow : Form
{
    private readonly MirrorSession _session;
    private readonly VideoView _view = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private D3DRenderer? _renderer;
    private long _lastPresented;
    private Size _frameSize;

    public SessionWindow(MirrorSession session)
    {
        _session = session;
        Text = $"{session.DeviceName} - Streamy";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_view);
        FitTo(session.Width > 0 ? new Size(session.Width, session.Height) : new Size(1080, 2340), center: true);

        _session.SizeChanged += (w, h) => BeginInvoke(() => FitTo(new Size(w, h), center: false));
        _session.Ended += ex => BeginInvoke(() =>
        {
            Text = $"{_session.DeviceName} - disconnected";
            if (ex is not null && !IsDisposed) MessageBox.Show(this, ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        _view.Resize += (_, _) => _renderer?.Resize(_view.ClientSize.Width, _view.ClientSize.Height);
        _timer.Tick += (_, _) => UpdateTitle();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_session.Decoder is not { } decoder) return;
        _renderer = new D3DRenderer(_view.Handle, decoder.Frames, _view.ClientSize.Width, _view.ClientSize.Height);
        _renderer.Failed += ex => BeginInvoke(() => MessageBox.Show(this, "Drawing failed: " + ex.Message, "Streamy", MessageBoxButtons.OK, MessageBoxIcon.Error));
        decoder.FrameDecoded += _renderer.Signal;
        _renderer.Signal();
        _timer.Start();
    }

    private void UpdateTitle()
    {
        if (_renderer is null) return;
        long presented = _renderer.PresentedFrames;
        Text = $"{_session.DeviceName} - {presented - _lastPresented} fps - Streamy";
        _lastPresented = presented;
    }

    /// <summary>
    /// Sizes the window to the phone's aspect ratio: on start, to 85% of the screen's working height (or width for
    /// a landscape phone); on rotation, keeping the window's current longer side.
    /// </summary>
    private void FitTo(Size frame, bool center)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame == _frameSize) return;
        _frameSize = frame;
        var area = Screen.FromControl(this).WorkingArea;
        var chrome = Size - ClientSize;

        int longSide = center
            ? (int)(frame.Height >= frame.Width ? area.Height * 0.85 : area.Width * 0.7)
            : Math.Max(ClientSize.Width, ClientSize.Height);
        double scale = (double)longSide / Math.Max(frame.Width, frame.Height);
        var client = new Size((int)(frame.Width * scale), (int)(frame.Height * scale));

        // Never larger than the screen.
        double shrink = Math.Min(1, Math.Min((double)(area.Width - chrome.Width) / client.Width, (double)(area.Height - chrome.Height) / client.Height));
        client = new Size((int)(client.Width * shrink), (int)(client.Height * shrink));

        var old = Bounds;
        ClientSize = client;
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
    }

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
        if (_session.Decoder is { } decoder && _renderer is not null) decoder.FrameDecoded -= _renderer.Signal;
        _renderer?.Dispose();
        await _session.DisposeAsync();
    }

    /// <summary>A plain child window Direct3D draws into; WinForms never paints it.</summary>
    private sealed class VideoView : Control
    {
        public VideoView()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        }
    }
}
