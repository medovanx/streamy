using Streamy.Core.Media;

namespace Streamy.Core.Tests;

/// <summary>Rows in the format of Android's "content query" (made-up library).</summary>
public class MediaStoreTests
{
    private const string Output = """
        Row: 0 _id=101, _data=/storage/emulated/0/DCIM/Camera/20260101_100000.heic, bucket_display_name=Camera, datetaken=1767261600000, date_modified=1767261600, _size=2000000, width=4080, height=3060, mime_type=image/heic
        Row: 1 _id=102, _data=/storage/emulated/0/DCIM/Screenshots/Screenshot_1.jpg, bucket_display_name=Screenshots, datetaken=1767348000000, date_modified=1767348000, _size=300000, width=1080, height=2340, mime_type=image/jpeg
        Row: 2 _id=103, _data=/storage/emulated/0/Pictures/Trip, Summer/beach, sunset.jpg, bucket_display_name=Trip, Summer, datetaken=NULL, date_modified=1767000000, _size=500000, width=2000, height=1500, mime_type=image/jpeg
        ---VIDEOS
        Row: 0 _id=201, _data=/storage/emulated/0/DCIM/Camera/20260102_120000.mp4, bucket_display_name=Camera, datetaken=1767355200000, date_modified=1767355200, _size=90000000, width=3840, height=2160, mime_type=video/mp4, duration=75500
        """;

    private static readonly IReadOnlyList<MediaItem> Items = MediaStore.Parse(Output);

    [Fact]
    public void ParsesPhotosAndVideosNewestFirst()
    {
        Assert.Equal([201L, 102L, 101L, 103L], Items.Select(i => i.Id));
        var video = Items[0];
        Assert.True(video.IsVideo);
        Assert.Equal("1:15", video.DurationText);
        Assert.Equal("content://media/external/video/media/201/thumbnail", video.ThumbnailUri);
        Assert.False(Items[1].IsVideo);
        Assert.Equal("content://media/external/images/media/102", Items[1].Uri);
    }

    [Fact]
    public void CommasInsidePathsAndAlbumsStayIntact()
    {
        var trip = Items.Single(i => i.Id == 103);
        Assert.Equal("/storage/emulated/0/Pictures/Trip, Summer/beach, sunset.jpg", trip.Path);
        Assert.Equal("Trip, Summer", trip.Album);
        Assert.Equal("beach, sunset.jpg", trip.Name);
        Assert.Equal("/storage/emulated/0/Pictures/Trip, Summer", trip.Folder);
        Assert.Equal("image/jpeg", trip.Mime);
    }

    [Fact]
    public void MissingDateTakenFallsBackToModified() =>
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767000000), Items.Single(i => i.Id == 103).Taken);

    [Fact]
    public void AlbumsStartWithEverythingAndVideos()
    {
        var albums = MediaStore.Albums(Items);
        Assert.Equal([MediaStore.AllAlbum, MediaStore.VideosAlbum, "Camera", "Screenshots", "Trip, Summer"], albums.Select(a => a.Name));
        Assert.Equal(4, albums[0].Items.Count);
        Assert.Equal(2, albums[2].Items.Count);
        Assert.Equal(201, albums[2].Cover!.Id);
    }

    [Fact]
    public void DeleteCommandBatchesByKind()
    {
        var cmd = MediaStore.DeleteCommand(Items);
        Assert.Contains("content delete --uri content://media/external/video/media --where \"_id IN (201)\"", cmd);
        Assert.Contains("content delete --uri content://media/external/images/media --where \"_id IN (102,101,103)\"", cmd);
    }

    [Fact]
    public void ScanCommandQuotesThePath() =>
        Assert.Equal("am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file:///sdcard/Pictures/a b.jpg'",
            MediaStore.ScanCommand("/sdcard/Pictures/a b.jpg"));

    [Fact]
    public void CacheKeyChangesWhenEdited()
    {
        var a = Items[1];
        Assert.NotEqual(a.CacheKey, (a with { Modified = a.Modified + 1 }).CacheKey);
        Assert.NotEqual(Items[0].CacheKey, (Items[0] with { IsVideo = false }).CacheKey);
    }

    [Fact]
    public void EmptyLibrary() => Assert.Empty(MediaStore.Parse("No result found.\n---VIDEOS\nNo result found."));
}
