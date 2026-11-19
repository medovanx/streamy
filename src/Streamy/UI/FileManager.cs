namespace Streamy.UI;

/// <summary>One file manager window per phone: opening it again brings the existing one forward.</summary>
internal static class FileManager
{
    private static readonly Dictionary<string, FileManagerWindow> Open_ = [];

    public static void Open(string serial, string deviceName)
    {
        if (Open_.TryGetValue(serial, out var existing) && !existing.IsDisposed)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new FileManagerWindow(serial, deviceName);
        Open_[serial] = window;
        window.FormClosed += (_, _) => Open_.Remove(serial);
        window.Show();
    }
}
