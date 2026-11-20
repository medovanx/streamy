namespace Streamy.Core.Media;

/// <summary>
/// Plans a gallery backup: every photo and video goes to the same place under the backup folder as on the phone
/// (DCIM\Camera\..., Pictures\Screenshots\...), so names never clash and the folder reads like the phone. A file
/// already there with the same size is skipped, so each backup only copies what is new; a copy cut short has the
/// wrong size and is copied again.
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

    /// <summary>The items to copy: those whose file is missing or a different size in the backup folder.</summary>
    public static IReadOnlyList<MediaItem> Plan(IEnumerable<MediaItem> items, Func<string, long?> sizeOnDisk) =>
        items.Where(i => sizeOnDisk(RelativePath(i)) is not { } size || (i.Size > 0 && size != i.Size)).ToList();
}
