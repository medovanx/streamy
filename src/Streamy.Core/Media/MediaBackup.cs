namespace Streamy.Core.Media;

/// <summary>
/// Plans a gallery backup: every photo and video goes to the same place under the backup folder as on the phone
/// (DCIM\Camera\..., Pictures\Screenshots\...), so names never clash and the folder reads like the phone. A file
/// already there is skipped when its size matches the phone's, or its date does (adb pull -a copies the date, and
/// the phone's media index sometimes has an out-of-date size), so each backup only copies what is new; a copy cut
/// short has neither and is copied again.
/// </summary>
public static class MediaBackup
{
    private static readonly string[] InternalRoots = ["/storage/emulated/0/", "/sdcard/", "/storage/self/primary/"];

    /// <summary>"DCIM/Camera/a.jpg" for internal storage, "SD card/DCIM/a.jpg" for a card, with Windows separators.</summary>
    public static string RelativePath(MediaItem item)
    {
        string path = item.Path;
        string rel;
        var root = InternalRoots.FirstOrDefault(r => path.StartsWith(r, StringComparison.Ordinal));
        if (root is not null)
        {
            rel = path[root.Length..];
        }
        else if (path.StartsWith("/storage/", StringComparison.Ordinal) && path.IndexOf('/', "/storage/".Length) is int slash and > 0)
        {
            rel = "SD card/" + path[(slash + 1)..]; // /storage/1234-ABCD/DCIM/...
        }
        else
        {
            rel = "Other/" + item.Name;
        }
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Safe);
        return string.Join('\\', parts);
    }

    /// <summary>Windows refuses some characters and names Android allows.</summary>
    private static string Safe(string part)
    {
        var chars = part.Select(c => c < 32 || "<>:\"|?*\\".Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd(' ', '.');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>The items to copy: those missing from the backup folder, or there with neither the size nor the date of the phone's file.</summary>
    public static IReadOnlyList<MediaItem> Plan(IEnumerable<MediaItem> items, Func<string, (long Size, long Modified)?> onDisk) =>
        items.Where(i => !IsBackedUp(i, onDisk(RelativePath(i)))).ToList();

    /// <summary>A file on disk (size, last write in Unix seconds) counts as this item's copy.</summary>
    public static bool IsBackedUp(MediaItem item, (long Size, long Modified)? file) =>
        file is { } f && (item.Size <= 0 || f.Size == item.Size || (item.Modified > 0 && Math.Abs(f.Modified - item.Modified) <= 2));
}
