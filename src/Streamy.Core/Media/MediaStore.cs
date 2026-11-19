using System.Globalization;
using System.Text.RegularExpressions;

namespace Streamy.Core.Media;

/// <summary>A photo or video in the phone's media library (Android's MediaStore).</summary>
public sealed record MediaItem(
    long Id,
    string Path,
    string Album,
    DateTimeOffset Taken,
    long Modified,
    long Size,
    int Width,
    int Height,
    string Mime,
    bool IsVideo,
    long DurationMs)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The folder the item lives in on the phone.</summary>
    public string Folder => Path[..Math.Max(1, Path.LastIndexOf('/'))];

    public string Uri => MediaStore.ItemUri(IsVideo, Id);
    public string ThumbnailUri => Uri + "/thumbnail";

    /// <summary>A file name for the thumbnail cache that changes when the photo is edited.</summary>
    public string CacheKey => $"{(IsVideo ? "v" : "i")}{Id}_{Modified}.jpg";

    public string DurationText
    {
        get
        {
            var t = TimeSpan.FromMilliseconds(DurationMs);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>An album: the items of one MediaStore bucket (usually one folder), newest first.</summary>
public sealed record MediaAlbum(string Name, IReadOnlyList<MediaItem> Items)
{
    public MediaItem? Cover => Items.Count > 0 ? Items[0] : null;
}

/// <summary>Reads the phone's media library with the shell's "content" command and groups it into albums.</summary>
public static partial class MediaStore
{
    private const string Columns = "_id:_data:bucket_display_name:datetaken:date_modified:_size:width:height:mime_type";
    public const string AllAlbum = "All photos and videos";
    public const string VideosAlbum = "Videos";

    /// <summary>One shell call that lists every photo, then every video.</summary>
    public static string QueryCommand =>
        $"content query --uri content://media/external/images/media --projection {Columns}; echo ---VIDEOS; " +
        $"content query --uri content://media/external/video/media --projection {Columns}:duration";

    public static string ItemUri(bool isVideo, long id) =>
        $"content://media/external/{(isVideo ? "video" : "images")}/media/{id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Deletes the items' rows and their files in one call per kind.</summary>
    public static string DeleteCommand(IEnumerable<MediaItem> items)
    {
        var parts = new List<string>();
        foreach (var group in items.GroupBy(i => i.IsVideo))
        {
            string ids = string.Join(",", group.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)));
            parts.Add($"content delete --uri content://media/external/{(group.Key ? "video" : "images")}/media --where \"_id IN ({ids})\"");
        }
        return string.Join("; ", parts);
    }

    /// <summary>Asks the media scanner to index a pushed file so it appears in the gallery.</summary>
    public static string ScanCommand(string devicePath) =>
        "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d " + Files.PhoneFiles.Quote("file://" + devicePath);

    /// <summary>Parses <see cref="QueryCommand"/> output, newest first.</summary>
    public static IReadOnlyList<MediaItem> Parse(string output)
    {
        var items = new List<MediaItem>();
        bool videos = false;
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("---VIDEOS", StringComparison.Ordinal)) { videos = true; continue; }
            if (!raw.StartsWith("Row: ", StringComparison.Ordinal)) continue;
            var f = ParseRow(raw);
            if (!long.TryParse(f.GetValueOrDefault("_id"), out var id) || f.GetValueOrDefault("_data") is not { Length: > 0 } path) continue;

            long modified = Long(f, "date_modified");
            long takenMs = Long(f, "datetaken");
            var taken = takenMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(takenMs) : DateTimeOffset.FromUnixTimeSeconds(modified);
            string album = f.GetValueOrDefault("bucket_display_name") is { Length: > 0 } b and not "NULL" ? b : FolderName(path);
            items.Add(new MediaItem(id, path, album, taken, modified, Long(f, "_size"), (int)Long(f, "width"), (int)Long(f, "height"),
                f.GetValueOrDefault("mime_type") ?? "", videos, Long(f, "duration")));
        }
        return items.OrderByDescending(i => i.Taken).ThenByDescending(i => i.Id).ToList();
    }

    /// <summary>
    /// The album list: everything, videos, then one album per bucket, the busiest and most recent first
    /// (Camera and Screenshots usually lead).
    /// </summary>
    public static IReadOnlyList<MediaAlbum> Albums(IReadOnlyList<MediaItem> items)
    {
        var list = new List<MediaAlbum> { new(AllAlbum, items) };
        var videos = items.Where(i => i.IsVideo).ToList();
        if (videos.Count > 0) list.Add(new MediaAlbum(VideosAlbum, videos));
        list.AddRange(items.GroupBy(i => i.Album, StringComparer.Ordinal)
            .Select(g => new MediaAlbum(g.Key, g.ToList()))
            .OrderByDescending(a => a.Items.Count)
            .ThenByDescending(a => a.Cover?.Taken));
        return list;
    }

    /// <summary>
    /// "Row: 3 _id=5, _data=/a, b/c.jpg, bucket_display_name=a, b" → fields. Values may contain ", ", so a field
    /// ends only where ", " is followed by one of the known column names and "=".
    /// </summary>
    public static Dictionary<string, string> ParseRow(string row)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var matches = KeyRegex().Matches(row);
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int start = m.Index + m.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : row.Length;
            fields[m.Groups[1].Value] = row[start..end];
        }
        return fields;
    }

    private static long Long(Dictionary<string, string> f, string key) =>
        long.TryParse(f.GetValueOrDefault(key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static string FolderName(string path)
    {
        var folder = path[..Math.Max(0, path.LastIndexOf('/'))];
        return folder[(folder.LastIndexOf('/') + 1)..];
    }

    [GeneratedRegex(@"(?:^Row: \d+ |, )(_id|_data|bucket_display_name|datetaken|date_modified|_size|width|height|mime_type|duration)=")]
    private static partial Regex KeyRegex();
}
