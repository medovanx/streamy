using Streamy.Core.Media;

namespace Streamy.Core.Tests;

public class MediaDetailsTests
{
    private const string Photo =
        "Row: 0 resolution=4080×2296, orientation=90, height=2296, owner_package_name=com.sec.android.app.camera, f_number=1.8, " +
        "date_modified=1794402711, mime_type=image/heic, _id=172020, iso=40, xmp=NULL, _size=831165, width=4080, is_favorite=1, " +
        "exposure_time=2.47525E-4, date_added=1794402711, _data=/storage/emulated/0/DCIM/Camera/a, b.heic";

    [Fact]
    public void CameraSettings()
    {
        var d = MediaDetails.Parse(Photo);
        Assert.Equal("f/1.8 · 1/4040 s · ISO 40", d.CameraSettings);
        Assert.Equal(90, d.Orientation);
        Assert.True(d.IsFavorite);
        Assert.Equal("Camera", d.CreatedBy);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794402711), d.Added);
        Assert.Equal("/storage/emulated/0/DCIM/Camera/a, b.heic", d.Fields["_data"]);
        Assert.False(d.Fields.ContainsKey("xmp")); // NULL values are left out
    }

    [Fact]
    public void ScreenshotHasNoCameraSettings()
    {
        var d = MediaDetails.Parse("Row: 0 _id=5, owner_package_name=com.android.systemui, orientation=0, is_favorite=0");
        Assert.Null(d.CameraSettings);
        Assert.Null(d.Orientation);
        Assert.False(d.IsFavorite);
        Assert.Equal("Screenshot", d.CreatedBy);
    }

    [Fact]
    public void VideoRates()
    {
        var d = MediaDetails.Parse("Row: 0 _id=7, bitrate=12400000, capture_framerate=29.97");
        Assert.Equal("12.4 Mbit/s", d.BitRate);
        Assert.Equal("29.97 fps", d.FrameRate);
    }

    [Theory]
    [InlineData(2.47525E-4, "1/4040 s")]
    [InlineData(0.01, "1/100 s")]
    [InlineData(0.5, "0.5 s")]
    [InlineData(2, "2 s")]
    [InlineData(30, "30 s")]
    public void Exposure(double seconds, string text) => Assert.Equal(text, MediaDetails.ExposureText(seconds));

    [Fact]
    public void NoRowGivesNothing() => Assert.Empty(MediaDetails.Parse("No result found.").Fields);

    [Fact]
    public void QueryAsksForOneItemOfTheRightKind()
    {
        var video = new MediaItem(42, "/x.mp4", "A", default, 0, 0, 0, 0, "video/mp4", true, 1000);
        Assert.Equal("content query --uri content://media/external/video/media --where '_id=42'", MediaDetails.QueryCommand(video));
    }
}
