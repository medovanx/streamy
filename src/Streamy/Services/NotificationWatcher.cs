using System.Diagnostics;
using System.Text;
using Streamy.Core.Adb;

namespace Streamy.Services;

/// <summary>
/// Watches one phone's notifications: one adb shell loop on the phone prints them every few seconds and new ones
/// are raised with the app's name. Stops when disposed or when the phone goes away.
/// </summary>
public sealed class NotificationWatcher : IDisposable
{
    private readonly string _serial;
    private readonly Process _process;
    private readonly NotificationTracker _tracker = new();
    private readonly Dictionary<string, string> _labels = [];
    private readonly StringBuilder _round = new();
    private bool _disposed;

    /// <summary>Raised on a worker thread: the phone, its app's name, and the notification.</summary>
    public event Action<string, string, PhoneNotification>? Posted;

    public bool HasExited => _process.HasExited;

    public NotificationWatcher(string serial)
    {
        _serial = serial;
        _process = AdbClient.Default.Start(["shell", PhoneNotifications.WatchCommand(5)], serial);
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _ = LoadLabelsAsync();
    }

    /// <summary>App names come from the helper's app list; until then (or if it fails) the package name is used.</summary>
    private async Task LoadLabelsAsync()
    {
        try
        {
            foreach (var app in await PhoneHelper.AppsAsync(_serial))
                lock (_labels) _labels[app.Package] = app.Label;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            Log.Info("notification labels: " + ex.Message);
        }
    }

    private void OnLine(string? line)
    {
        if (line is null || _disposed) return;
        if (line.Trim() != PhoneNotifications.EndMarker)
        {
            _round.AppendLine(line);
            return;
        }
        var round = PhoneNotifications.Parse(_round.ToString());
        _round.Clear();
        foreach (var n in _tracker.Next(round))
        {
            string label;
            lock (_labels) label = _labels.GetValueOrDefault(n.Package, n.Package);
            Posted?.Invoke(_serial, label, n);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
