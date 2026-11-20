using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Streamy.DevTools;

/// <summary>
/// Drops a data object on a folder through Explorer's own drop target, the same object Explorer uses when a file
/// is dropped on that folder's window, so a drag can be tested without moving the real mouse.
/// </summary>
internal static class ShellDrop
{
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        void DragEnter(ComDataObject data, int keyState, long point, ref int effect);
        void DragOver(int keyState, long point, ref int effect);
        void DragLeave();
        void Drop(ComDataObject data, int keyState, long point, ref int effect);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr bc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attributes);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumList);
        void BindToObject(IntPtr pidl, IntPtr bc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr bc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
        void GetUIObjectOf(IntPtr hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr ppv);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv, out IntPtr pidlLast);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>Enter, over and drop at the folder; returns how the target described the drop.</summary>
    public static int DropOn(string folder, ComDataObject data, Action<string> log, Action? beforeDrop = null)
    {
        SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _);
        try
        {
            var iidShellFolder = typeof(IShellFolder).GUID;
            SHBindToParent(pidl, ref iidShellFolder, out var parent, out var last);
            var iidDrop = typeof(IDropTarget).GUID;
            parent.GetUIObjectOf(IntPtr.Zero, 1, [last], ref iidDrop, IntPtr.Zero, out var ptr);
            var target = (IDropTarget)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            const int MK_LBUTTON = 1;
            int effect = (int)DragDropEffects.Copy;
            target.DragEnter(data, MK_LBUTTON, 0, ref effect);
            log($"after DragEnter: effect {effect}");
            effect = (int)DragDropEffects.Copy;
            target.DragOver(MK_LBUTTON, 0, ref effect);
            log($"after DragOver: effect {effect}");
            effect = (int)DragDropEffects.Copy;
            beforeDrop?.Invoke();
            target.Drop(data, 0, 0, ref effect);
            log($"after Drop: effect {effect}");
            return effect;
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }
}
