using System.Buffers.Binary;
using System.Text;
using Streamy.Core.Media;

namespace Streamy.Core.Tests;

public class GeoTagsTests
{
    /// <summary>Builds a minimal little-endian TIFF with IFD0 → GPS IFD holding latitude/longitude.</summary>
    private static byte[] Tiff(double lat, char latRef, double lon, char lonRef, bool bigEndian = false)
    {
        var b = new byte[200];
        void U16(int o, int v) { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)v); }
        void U32(int o, uint v) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); }
        b[0] = b[1] = (byte)(bigEndian ? 'M' : 'I');
        U16(2, 42);
        U32(4, 8);
        U16(8, 1);                       // IFD0: one entry
        U16(10, 0x8825); U16(12, 4); U32(14, 1); U32(18, 30); // GPS IFD at 30
        U16(30, 4);                      // GPS IFD: four entries
        U16(32, 1); U16(34, 2); U32(36, 2); b[40] = (byte)latRef;
        U16(44, 2); U16(46, 5); U32(48, 3); U32(52, 100);
        U16(56, 3); U16(58, 2); U32(60, 2); b[64] = (byte)lonRef;
        U16(68, 4); U16(70, 5); U32(72, 3); U32(76, 124);
        void Dms(int o, double v)
        {
            int deg = (int)v; double minF = (v - deg) * 60; int min = (int)minF; double sec = (minF - min) * 60;
            U32(o, (uint)deg); U32(o + 4, 1); U32(o + 8, (uint)min); U32(o + 12, 1); U32(o + 16, (uint)Math.Round(sec * 1000)); U32(o + 20, 1000);
        }
        Dms(100, lat);
        Dms(124, lon);
        return b;
    }

    [Fact]
    public void ReadsExifGpsInAJpegHeader()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0, 0 }.Concat("Exif\0\0"u8.ToArray()).Concat(Tiff(30.0444, 'N', 31.2357, 'E')).ToArray();
        var p = GeoTags.Read(jpeg, isVideo: false);
        Assert.NotNull(p);
        Assert.Equal(30.0444, p!.Value.Latitude, 3);
        Assert.Equal(31.2357, p.Value.Longitude, 3);
    }

    [Fact]
    public void SouthAndWestAreNegative()
    {
        var data = "xxxxExif\0\0"u8.ToArray().Concat(Tiff(33.8688, 'S', 151.2093, 'W')).ToArray();
        var p = GeoTags.ReadExif(data)!.Value;
        Assert.True(p.Latitude < 0 && p.Longitude < 0);
    }

    [Fact]
    public void BigEndianTiff() =>
        Assert.Equal(48.8566, GeoTags.ReadExif("Exif\0\0"u8.ToArray().Concat(Tiff(48.8566, 'N', 2.3522, 'E', bigEndian: true)).ToArray())!.Value.Latitude, 3);

    [Fact]
    public void NoGpsGivesNothing()
    {
        Assert.Null(GeoTags.ReadExif("Exif\0\0II*\0\u0008\0\0\0\0\0"u8.ToArray()));
        Assert.Null(GeoTags.ReadExif(new byte[100]));
    }

    [Fact]
    public void ReadsMp4Location()
    {
        var data = new byte[] { 0, 0, 0, 30, 0xA9, (byte)'x', (byte)'y', (byte)'z', 0, 18, 0x15, 0xC7 }
            .Concat(Encoding.ASCII.GetBytes("+31.2448+031.4414/")).ToArray();
        var p = GeoTags.Read(data, isVideo: true)!.Value;
        Assert.Equal(31.2448, p.Latitude, 4);
        Assert.Equal(31.4414, p.Longitude, 4);
    }

    [Fact]
    public void ZeroZeroIsNotAPlace() => Assert.False(new GeoPoint(0, 0).IsValid);

    [Fact]
    public void SlicesFromTheRightEnd()
    {
        MediaItem Item(string name, bool video) => new(1, "/sdcard/DCIM/" + name, "Camera", default, 0, 1, 0, 0, "", video, 0);
        Assert.False(GeoTags.Slice(Item("a.jpg", false)).FromEnd);
        Assert.True(GeoTags.Slice(Item("a.heic", false)).FromEnd);
        Assert.True(GeoTags.Slice(Item("a.mp4", true)).FromEnd);
        Assert.False(GeoTags.MayHaveLocation(Item("Screenshot.png", false)));
    }

    [Fact]
    public void ParsesABatchStream()
    {
        var a = "Exif\0\0"u8.ToArray().Concat(Tiff(30.0444, 'N', 31.2357, 'E')).ToArray();
        var b = new byte[] { 1, 2, 3 };
        var stream = Encoding.ASCII.GetBytes($"@@i5 {a.Length}\n").Concat(a)
            .Concat(Encoding.ASCII.GetBytes($"@@i6 {b.Length}\n")).Concat(b)
            .Concat(Encoding.ASCII.GetBytes("@@v7 0\n")).ToArray();
        var (found, complete) = GeoTags.ParseBatch(stream, expected: 3);
        Assert.True(complete);
        Assert.Equal(3, found.Count);
        Assert.NotNull(found["i5"]);
        Assert.Null(found["i6"]);
        Assert.Null(found["v7"]);
    }

    [Fact]
    public void ABrokenFrameIsNoticed()
    {
        // The first file was one byte shorter than announced: the next header is misaligned.
        var stream = Encoding.ASCII.GetBytes("@@i1 4\n").Concat(new byte[] { 1, 2, 3 })
            .Concat(Encoding.ASCII.GetBytes("@@i2 2\n")).Concat(new byte[] { 9, 9 }).ToArray();
        var (found, complete) = GeoTags.ParseBatch(stream, expected: 2);
        Assert.False(complete);
        Assert.True(found.Count <= 1);
    }

    [Fact]
    public void FewerItemsThanExpectedIsIncomplete() =>
        Assert.False(GeoTags.ParseBatch(Encoding.ASCII.GetBytes("@@i1 0\n"), expected: 2).Complete);

    [Fact]
    public void FastCommandUsesKnownSizesAndOneProcessPerFile()
    {
        var items = new[]
        {
            new MediaItem(5, "/sdcard/DCIM/it's.jpg", "A", default, 0, 1_000_000, 0, 0, "", false, 0),
            new MediaItem(6, "/sdcard/DCIM/b.heic", "A", default, 0, 10_000, 0, 0, "", false, 0),
        };
        var cmd = GeoTags.BatchCommand(items);
        Assert.Contains("echo \"@@i5 24576\"; head -c 24576 '/sdcard/DCIM/it'\\''s.jpg'", cmd);
        Assert.Contains("echo \"@@i6 10000\"; tail -c 10000 '/sdcard/DCIM/b.heic'", cmd);
        Assert.DoesNotContain("stat", cmd);
        Assert.Contains("stat -c %s", GeoTags.BatchCommand(items, exact: true));
    }
}

public class GeocoderTests
{
    private static readonly Geocoder Small = new(
        ["Cairo\tEG\t30.0626\t31.2497", "Giza\tEG\t30.0081\t31.2109", "Alexandria\tEG\t31.2018\t29.9158", "Paris\tFR\t48.8534\t2.3488"],
        ["EG\tEgypt", "FR\tFrance"]);

    [Fact]
    public void NearestCity()
    {
        Assert.Equal("Cairo, Egypt", Small.Name(new GeoPoint(30.0444, 31.2357)));
        Assert.Equal("Alexandria, Egypt", Small.Name(new GeoPoint(31.2, 29.92)));
        Assert.Equal("Paris, France", Small.Name(new GeoPoint(48.86, 2.35)));
    }

    [Fact]
    public void FarFromCitiesIsUnnamed() => Assert.Null(Small.Name(new GeoPoint(-40, -120)));

    [Fact]
    public void BuiltInListLoads()
    {
        var g = Geocoder.Default;
        Assert.True(g.CityCount > 30000);
        Assert.EndsWith("Egypt", g.Name(new GeoPoint(30.0444, 31.2357)));
        Assert.EndsWith("Japan", g.Name(new GeoPoint(35.6762, 139.6503)));
    }
}
