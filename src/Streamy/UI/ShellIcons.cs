using System.Runtime.InteropServices;

namespace Streamy.UI;

/// <summary>Windows' own small icons for a folder or a file extension (no file needs to exist), cached per extension.</summary>
internal sealed class ShellIcons : IDisposable
{
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const string FolderKey = "<folder>";

    public ImageList List { get; } = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };

    /// <summary>The image key for a folder, or for a file with this extension ("" for none).</summary>
    public string Key(bool isDirectory, string extension)
    {
        string key = isDirectory ? FolderKey : "." + extension;
        if (List.Images.ContainsKey(key)) return key;
        var info = new SHFILEINFO();
        string name = isDirectory ? "folder" : "file" + key;
        IntPtr ok = SHGetFileInfo(name, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
        if (ok != IntPtr.Zero && info.hIcon != IntPtr.Zero)
        {
            using var icon = Icon.FromHandle(info.hIcon);
            List.Images.Add(key, (Icon)icon.Clone());
            DestroyIcon(info.hIcon);
        }
        return key;
    }

    public void Dispose() => List.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
