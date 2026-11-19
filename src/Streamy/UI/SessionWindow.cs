using Streamy.Core.Protocol;
using Streamy.Session;

namespace Streamy.UI;

/// <summary>
/// One window per mirrored phone. In 0.1.0 it shows the live stream statistics; the decoded picture,
/// mouse and keyboard input arrive with the video milestone (see docs/SRS.md).
/// </summary>
internal sealed class SessionWindow : Form
{
    private readonly MirrorSession _session;
    private readonly Label _info = new() { Dock = DockStyle.Fill, Padding = new Padding(16), Font = new Font("Segoe UI", 10f) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private long _lastVideoBytes;
    private string _clipboard = "";

    public SessionWindow(MirrorSession session)
    {
        _session = session;
        Text = $"{session.DeviceName} - Streamy";
        ClientSize = new Size(420, 260);
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_info);

        _session.DeviceMessageReceived += m =>
        {
            if (m is DeviceMessage.Clipboard c) _clipboard = c.Text;
        };
        _session.Ended += ex => BeginInvoke(() =>
        {
            _timer.Stop();
            _info.Text += "\n\nSession ended" + (ex is null ? "." : ": " + ex.Message);
        });
        _timer.Tick += (_, _) => Render();
        _timer.Start();
        Render();
    }

    private void Render()
    {
        long bytes = Interlocked.Read(ref _session.VideoStats.Bytes);
        long rate = bytes - _lastVideoBytes;
        _lastVideoBytes = bytes;
        _info.Text =
            $"{_session.DeviceName}  ({_session.Serial})\n\n" +
            $"Video: {_session.VideoCodec} {_session.Width}x{_session.Height}, {_session.VideoStats.Packets} packets, {rate * 8 / 1000} kbit/s\n" +
            $"Audio: {(_session.AudioCodec == default ? "off" : _session.AudioCodec.ToString())}, {_session.AudioStats.Packets} packets\n" +
            (_clipboard.Length > 0 ? $"Phone clipboard: {Shorten(_clipboard)}\n" : "") +
            "\nThe picture and input come in the next milestone.";
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "...";

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
        await _session.DisposeAsync();
    }
}
