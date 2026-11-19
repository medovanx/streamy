namespace Streamy.UI;

/// <summary>One file manager window per phone: opening it again brings the existing one forward.</summary>
internal static class FileManager
{
    private static readonly Dictionary<string, Form> Windows = [];

    public static void Open(string serial, string deviceName) => Show("files:" + serial, () => new FileManagerWindow(serial, deviceName));

    public static void OpenGallery(string serial, string deviceName) => Show("gallery:" + serial, () => new GalleryWindow(serial, deviceName));

    private static void Show(string key, Func<Form> create)
    {
        if (Windows.TryGetValue(key, out var existing) && !existing.IsDisposed)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }
        var window = create();
        Windows[key] = window;
        window.FormClosed += (_, _) => Windows.Remove(key);
        window.Show();
    }
}
