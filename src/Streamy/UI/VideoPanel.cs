using System.Globalization;
using Streamy.Video;

namespace Streamy.UI;

/// <summary>
/// A video area with its own controls: play/pause, a seek bar with times, and mute. Drag sideways on the
/// picture to swipe to the next or previous item, like on photos.
/// </summary>
internal sealed class VideoPanel : Panel
{
    private readonly Surface _surface = new() { Dock = DockStyle.Fill };
    private readonly Panel _controls = new() { Dock = DockStyle.Bottom, Height = 40, BackColor = Color.FromArgb(0x14, 0x14, 0x14) };
    private readonly Button _play = new() { Dock = DockStyle.Left, Width = 44, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, TabStop = false };
    private readonly Button _mute = new() { Dock = DockStyle.Right, Width = 44, FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, TabStop = false };
    private readonly Label _time = new() { Dock = DockStyle.Right, Width = 120, ForeColor = Color.Gainsboro, TextAlign = ContentAlignment.MiddleCenter };
    private readonly TrackBar _seek = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, TabStop = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private VideoFilePlayer? _player;
    private bool _seeking;
    private int? _dragFrom;

    /// <summary>Raised with +1 or -1 when the user swipes on the video.</summary>
    public event Action<int>? Swiped;

    /// <summary>Raised on the UI thread with an error message when the video cannot be played.</summary>
    public event Action<string>? Failed;

    public VideoPanel()
    {
        BackColor = Color.Black;
        foreach (var b in new[] { _play, _mute }) b.FlatAppearance.BorderSize = 0;
        Glyph.Apply(_play, Glyph.Pause);
        Glyph.Apply(_mute, Glyph.Speaker);
        _play.Text = _mute.Text = "";
        _controls.Controls.Add(_seek);
        _controls.Controls.Add(_time);
        _controls.Controls.Add(_mute);
        _controls.Controls.Add(_play);
        Controls.Add(_surface);
        Controls.Add(_controls);

        _play.Click += (_, _) => TogglePause();
        _mute.Click += (_, _) =>
        {
            if (_player is null) return;
            _player.Muted = !_player.Muted;
            Glyph.Apply(_mute, _player.Muted ? Glyph.Muted : Glyph.Speaker);
            _mute.Text = "";
        };
        _seek.MouseDown += (_, _) => _seeking = true;
        _seek.MouseUp += (_, _) => { _seeking = false; SeekToBar(); };
        _seek.KeyUp += (_, _) => SeekToBar();
        _timer.Tick += (_, _) => UpdateControls();
        _surface.Resize += (_, _) => _player?.Resize(_surface.ClientSize.Width, _surface.ClientSize.Height);
        _surface.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _dragFrom = e.X; };
        _surface.MouseUp += (_, e) =>
        {
            if (_dragFrom is not { } from) return;
            _dragFrom = null;
            int dx = e.X - from, threshold = _surface.Width / 5;
            if (dx < -threshold) Swiped?.Invoke(1);
            else if (dx > threshold) Swiped?.Invoke(-1);
            else if (Math.Abs(dx) < 6) TogglePause(); // a click pauses, like on phones
        };
    }

    public void Play(string path)
    {
        if (_player is null)
        {
            _player = new VideoFilePlayer(_surface.Handle, _surface.ClientSize.Width, _surface.ClientSize.Height);
            _player.Ended += error =>
            {
                if (error is null || IsDisposed) return;
                BeginInvoke(() => Failed?.Invoke("Windows could not play this video: " + error));
            };
        }
        _player.Play(path);
        _timer.Start();
        UpdateControls();
    }

    public void Stop()
    {
        _timer.Stop();
        _player?.Stop();
    }

    public void TogglePause()
    {
        if (_player is null) return;
        _player.TogglePause();
        UpdateControls();
    }

    private void SeekToBar()
    {
        if (_player is null || _player.Duration <= TimeSpan.Zero) return;
        _player.Position = TimeSpan.FromMilliseconds(_player.Duration.TotalMilliseconds * _seek.Value / _seek.Maximum);
    }

    private void UpdateControls()
    {
        if (_player is null) return;
        var duration = _player.Duration;
        var position = _player.Position;
        if (!_seeking && duration > TimeSpan.Zero)
            _seek.Value = (int)Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds * _seek.Maximum, 0, _seek.Maximum);
        _time.Text = $"{Format(position)} / {Format(duration)}";
        Glyph.Apply(_play, _player.IsPlaying ? Glyph.Pause : Glyph.Play);
        _play.Text = "";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _player?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The child window the video is drawn into; WinForms never paints it.</summary>
    private sealed class Surface : Control
    {
        public Surface()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            Cursor = Cursors.Hand;
        }
    }
}
