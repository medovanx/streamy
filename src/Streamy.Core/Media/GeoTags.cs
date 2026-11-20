using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Streamy.Core.Media;

/// <summary>A place on Earth in decimal degrees.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 && !(Latitude == 0 && Longitude == 0);

    public override string ToString() =>
        $"{Latitude.ToString("0.00000", CultureInfo.InvariantCulture)}, {Longitude.ToString("0.00000", CultureInfo.InvariantCulture)}";

    /// <summary>Great-circle distance in kilometres.</summary>
    public double DistanceKm(GeoPoint other)
    {
        const double R = 6371;
        double dLat = (other.Latitude - Latitude) * Math.PI / 180, dLon = (other.Longitude - Longitude) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Latitude * Math.PI / 180) * Math.Cos(other.Latitude * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>
/// Reads where a photo or video was taken from a slice of the file, without the rest of it: EXIF GPS in JPEG and
/// HEIC (the TIFF block after "Exif\0\0"), and the ISO 6709 "©xyz" tag MP4 cameras write ("+31.2448+031.4414/").
/// </summary>
public static partial class GeoTags
{
    /// <summary>
    /// How much of a file to read and from which end: JPEG keeps EXIF at the start, Samsung HEIC at the end,
    /// and camera MP4s put their "moov" box (with ©xyz) at the end.
    /// </summary>
    public static (bool FromEnd, int Bytes) Slice(MediaItem item)
    {
        if (item.IsVideo) return (true, 128 * 1024);
        string ext = Path.GetExtension(item.Name).ToLowerInvariant();
        return ext is ".heic" or ".heif" or ".avif" ? (true, 40 * 1024) : (false, 24 * 1024);
    }

    /// <summary>Photos and videos that can carry a location (screenshots in PNG and GIFs cannot).</summary>
    public static bool MayHaveLocation(MediaItem item)
    {
        if (item.IsVideo) return true;
        string ext = Path.GetExtension(item.Name).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".avif" or ".dng" or ".webp";
    }

    public static GeoPoint? Read(ReadOnlySpan<byte> data, bool isVideo) =>
        isVideo ? ReadIso6709(data) : ReadExif(data);

    // ---- MP4 ----

    public static GeoPoint? ReadIso6709(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> tag = [0xA9, (byte)'x', (byte)'y', (byte)'z'];
        int at = data.IndexOf(tag);
        if (at < 0) return null;
        var window = data.Slice(at, Math.Min(64, data.Length - at));
        var m = Iso6709().Match(Encoding.ASCII.GetString(window));
        if (!m.Success) return null;
        var p = new GeoPoint(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return p.IsValid ? p : null;
    }

    [GeneratedRegex(@"([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)")]
    private static partial Regex Iso6709();

    // ---- EXIF ----

    public static GeoPoint? ReadExif(ReadOnlySpan<byte> data)
    {
        // Every "Exif\0\0" in the slice is a candidate (HEIC may hold more than one metadata item).
        ReadOnlySpan<byte> marker = "Exif\0\0"u8;
        int from = 0;
        while (from < data.Length)
        {
            int at = data[from..].IndexOf(marker);
            if (at < 0) return null;
            at += from;
            if (ReadTiff(data[(at + 6)..]) is { } p) return p;
            from = at + 6;
        }
        return null;
    }

    /// <summary>Parses a TIFF block: IFD0 → GPS IFD (tag 0x8825) → latitude and longitude with their N/S/E/W refs.</summary>
    public static GeoPoint? ReadTiff(ReadOnlySpan<byte> block)
    {
        // A copy, so the small readers below can use it (local functions cannot capture spans). EXIF is a few KB.
        byte[] tiff = block.ToArray();
        if (tiff.Length < 8) return null;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return null;

        uint U32(int o) => o + 4 <= tiff.Length ? (little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(o))) : 0;
        ushort U16(int o) => o + 2 <= tiff.Length ? (little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(o))) : (ushort)0;
        if (U16(2) != 42) return null;

        int ifd0 = (int)U32(4);
        int gps = 0;
        int count = U16(ifd0);
        for (int i = 0; i < count; i++)
        {
            int e = ifd0 + 2 + i * 12;
            if (e + 12 > tiff.Length) return null;
            if (U16(e) == 0x8825) { gps = (int)U32(e + 8); break; }
        }
        if (gps <= 0 || gps + 2 > tiff.Length) return null;

        char latRef = 'N', lonRef = 'E';
        double? lat = null, lon = null;
        int n = U16(gps);
        for (int i = 0; i < n; i++)
        {
            int e = gps + 2 + i * 12;
            if (e + 12 > tiff.Length) break;
            ushort tag = U16(e), type = U16(e + 2);
            switch (tag)
            {
                case 1: latRef = (char)tiff[e + 8]; break;              // ASCII, fits in the entry
                case 3: lonRef = (char)tiff[e + 8]; break;
                case 2 when type == 5: lat = Degrees((int)U32(e + 8)); break; // 3 RATIONALs at an offset
                case 4 when type == 5: lon = Degrees((int)U32(e + 8)); break;
            }
        }
        if (lat is null || lon is null) return null;
        var p = new GeoPoint(latRef == 'S' ? -lat.Value : lat.Value, lonRef == 'W' ? -lon.Value : lon.Value);
        return p.IsValid ? p : null;

        double? Degrees(int o)
        {
            if (o <= 0 || o + 24 > tiff.Length) return null;
            double R(int k) { uint num = U32(o + k * 8), den = U32(o + k * 8 + 4); return den == 0 ? 0 : (double)num / den; }
            return R(0) + R(1) / 60 + R(2) / 3600;
        }
    }

    // ---- Reading many files in one adb call ----

    /// <summary>
    /// A shell script that prints, for each item, a header line "@@ID LENGTH" followed by LENGTH raw bytes from the
    /// start or end of its file. Meant for "adb exec-out", which passes bytes untouched.
    /// <para>
    /// The fast form takes each file's size from the media library, so the phone only starts one head or tail per
    /// file (the header is the shell's built-in echo). A missing file is padded with zeros to keep the framing; a
    /// file whose size changed since it was indexed breaks the framing, which <see cref="ParseBatch"/> notices, and
    /// the batch is then read again with <paramref name="exact"/>, which asks the phone for every size.
    /// </para>
    /// </summary>
    public static string BatchCommand(IEnumerable<MediaItem> items, bool exact = false)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            var (fromEnd, bytes) = Slice(item);
            string q = Files.PhoneFiles.Quote(item.Path);
            string key = Key(item);
            string read = fromEnd ? "tail" : "head";
            if (exact)
            {
                sb.Append($"s=$(stat -c %s {q} 2>/dev/null || echo 0); n=$(( s < {bytes} ? s : {bytes} )); echo \"@@{key} $n\"; ");
                sb.Append($"{read} -c $n {q} 2>/dev/null; ");
            }
            else
            {
                long n = Math.Max(0, Math.Min(item.Size, bytes));
                sb.Append($"echo \"@@{key} {n}\"; {read} -c {n} {q} 2>/dev/null || head -c {n} /dev/zero; ");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits <see cref="BatchCommand"/> output into (kind+id, location). Complete is false when the stream lost its
    /// framing (a file changed size) or ended early; whatever was read before that is still returned.
    /// </summary>
    public static (Dictionary<string, GeoPoint?> Found, bool Complete) ParseBatch(ReadOnlySpan<byte> output, int expected = -1)
    {
        var result = new Dictionary<string, GeoPoint?>(StringComparer.Ordinal);
        int pos = 0;
        while (pos < output.Length)
        {
            int nl = output[pos..].IndexOf((byte)'\n');
            if (nl < 0 || nl > 64) return (result, false);
            var header = Encoding.ASCII.GetString(output.Slice(pos, nl)).Trim();
            if (!header.StartsWith("@@", StringComparison.Ordinal)) return (result, false);
            pos += nl + 1;
            var parts = header[2..].Split(' ');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int len) || len < 0 || pos + len > output.Length) return (result, false);
            result[parts[0]] = Read(output.Slice(pos, len), parts[0].StartsWith('v'));
            pos += len;
        }
        return (result, expected < 0 || result.Count == expected);
    }

    public static string Key(MediaItem item) => (item.IsVideo ? "v" : "i") + item.Id.ToString(CultureInfo.InvariantCulture);
}
