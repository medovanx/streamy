using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Streamy.Core.Media;
using Streamy.Services;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Streamy.UI;

/// <summary>
/// Dragging photos and videos out of the gallery to Explorer, the desktop or another app. The files are only
/// copied from the phone when the drop target asks for them (at the drop), into a temporary folder, and the
/// target then copies them from there. Items already in the viewer's cache are not copied from the phone again.
/// <para>
/// This is the Windows (OLE) data object itself rather than a WinForms DataObject: WinForms answers the drop
/// target from its own store and never calls back, so a file list made on request has to be served here.
/// </para>
/// </summary>
internal sealed class PhoneFileDrag : ComDataObject
{
    private const short CF_HDROP = 15;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Streamy drag");
    private readonly string _serial;
    private readonly IReadOnlyList<MediaItem> _items;
    private readonly DataObject _inner = new();
    private readonly ComDataObject _innerCom;
    private string[]? _files;

    /// <summary>
    /// Set when the button is released over a target. Before that, targets asking for the files (Explorer does as
    /// soon as the drag enters its window) get the paths the files will have, and nothing is copied yet.
    /// </summary>
    public bool Dropping { get; set; }

    /// <summary>For the developer switch: how many times a target asked for the files.</summary>
    public int Requests { get; private set; }

    public PhoneFileDrag(string serial, IReadOnlyList<MediaItem> items)
    {
        _serial = serial;
        _items = items;
        // The inner object lists the formats (files, preferred effect: copy) and serves everything but the files.
        _inner.SetData(DataFormats.FileDrop, Array.Empty<string>());
        _inner.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        _innerCom = _inner;
    }

    // ---- Drag source ---------------------------------------------------------------------------------------

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    private sealed class DropSource(PhoneFileDrag owner) : IDropSource
    {
        private const int DRAGDROP_S_DROP = 0x40100, DRAGDROP_S_CANCEL = 0x40101, DRAGDROP_S_USEDEFAULTCURSORS = 0x40102;
        private const int MK_LBUTTON = 1;

        public int QueryContinueDrag(int escapePressed, int keyState)
        {
            if (escapePressed != 0) return DRAGDROP_S_CANCEL;
            if ((keyState & MK_LBUTTON) != 0) return 0;
            owner.Dropping = true;
            return DRAGDROP_S_DROP;
        }

        public int GiveFeedback(int effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComDataObject data, IDropSource source, int okEffects, out int effect);

    /// <summary>Runs the drag until the button is released; true when something took the files.</summary>
    public bool Run()
    {
        int hr = DoDragDrop(this, new DropSource(this), (int)DragDropEffects.Copy, out int effect);
        return hr == 0x40100 && effect != 0;
    }

    // ---- IDataObject ---------------------------------------------------------------------------------------

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (format.cfFormat == CF_HDROP && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            Requests++;
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = DropFiles(Files()) };
            return;
        }
        _innerCom.GetData(ref format, out medium);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => _innerCom.GetDataHere(ref format, ref medium);

    public int QueryGetData(ref FORMATETC format) => _innerCom.QueryGetData(ref format);

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) => _innerCom.GetCanonicalFormatEtc(ref formatIn, out formatOut);

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => _innerCom.SetData(ref formatIn, ref medium, release);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => _innerCom.EnumFormatEtc(direction);

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) => _innerCom.DAdvise(ref format, advf, sink, out connection);

    public void DUnadvise(int connection) => _innerCom.DUnadvise(connection);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) => _innerCom.EnumDAdvise(out enumAdvise);

    /// <summary>A DROPFILES block: the header, then the paths as UTF-16, each ending in a null, and a final null.</summary>
    private static IntPtr DropFiles(string[] files)
    {
        const int header = 20;
        var text = string.Concat(files.Select(f => f + "\0")) + "\0";
        var bytes = System.Text.Encoding.Unicode.GetBytes(text);
        var h = Marshal.AllocHGlobal(header + bytes.Length);
        Marshal.WriteInt32(h, 0, header);     // pFiles: offset of the list
        Marshal.WriteInt64(h, 4, 0);          // pt
        Marshal.WriteInt32(h, 12, 0);         // fNC
        Marshal.WriteInt32(h, 16, 1);         // fWide
        Marshal.Copy(bytes, 0, h + header, bytes.Length);
        return h;
    }

    // ---- The files -----------------------------------------------------------------------------------------

    private string? _dir;
    private string[]? _planned;

    /// <summary>Where each item will be: a folder of its own for this drag, the phone's file names.</summary>
    private string[] Planned()
    {
        if (_planned is not null) return _planned;
        _dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return _planned = _items.Select(i => Path.Combine(_dir, Unique(names, i.Name))).ToArray();
    }

    /// <summary>
    /// Before the drop: the planned paths. At the drop: copies the items once, blocking, since the target waits for
    /// the paths, and returns those that arrived.
    /// </summary>
    private string[] Files()
    {
        if (!Dropping) return Planned();
        if (_files is not null) return _files;
        var planned = Planned();
        var dir = _dir!;
        Directory.CreateDirectory(dir);
        var result = new List<string>();
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            Task.Run(async () =>
            {
                var toPull = new List<MediaItem>();
                for (int k = 0; k < _items.Count; k++)
                {
                    var item = _items[k];
                    var target = planned[k];
                    var name = Path.GetFileName(target);
                    if (MediaCache.Cached(_serial, item) is { } cached) File.Copy(cached, target);
                    else if (name == item.Name) toPull.Add(item);
                    else await AdbClient.Default.RunAsync(["pull", "-a", item.Path, target], _serial, TimeSpan.FromHours(1));
                    result.Add(target);
                }
                foreach (var batch in toPull.Chunk(40))
                    await AdbClient.Default.RunAsync(["pull", "-a", .. batch.Select(i => i.Path), dir], _serial, TimeSpan.FromHours(1));
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "drag out");
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
        return _files = result.Where(File.Exists).ToArray();
    }

    private static string Unique(HashSet<string> names, string name)
    {
        var candidate = name;
        for (int n = 2; !names.Add(candidate); n++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
        return candidate;
    }

    /// <summary>Removes drag folders from earlier days; the drop targets have long finished copying them.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var d in Directory.GetDirectories(Root))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(d, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
