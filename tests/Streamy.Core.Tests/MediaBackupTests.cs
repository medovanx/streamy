using Streamy.Core.Media;

namespace Streamy.Core.Tests;

public class MediaBackupTests
{
    private static MediaItem Item(string path, long size = 100) =>
        new(1, path, "Album", DateTimeOffset.UnixEpoch, 0, size, 0, 0, "image/jpeg", false, 0);

    [Theory]
    [InlineData("/storage/emulated/0/DCIM/Camera/a.jpg", @"DCIM\Camera\a.jpg")]
    [InlineData("/sdcard/Pictures/Screenshots/s.png", @"Pictures\Screenshots\s.png")]
    [InlineData("/storage/1234-ABCD/DCIM/b.jpg", @"SD card\DCIM\b.jpg")]
    [InlineData("/data/odd/c.jpg", @"Other\c.jpg")]
    [InlineData("/storage/emulated/0/Pictures/What? Now:/x|y.jpg", @"Pictures\What_ Now_\x_y.jpg")]
    [InlineData("/storage/emulated/0/Pictures/dots.../a.jpg", @"Pictures\dots\a.jpg")]
    public void MirrorsThePhonesFolders(string path, string expected) => Assert.Equal(expected, MediaBackup.RelativePath(Item(path)));

    [Fact]
    public void CopiesOnlyNewOrIncompleteFiles()
    {
        var have = new Dictionary<string, long> { [@"DCIM\a.jpg"] = 100, [@"DCIM\b.jpg"] = 40 };
        var plan = MediaBackup.Plan(
            [Item("/sdcard/DCIM/a.jpg"), Item("/sdcard/DCIM/b.jpg"), Item("/sdcard/DCIM/c.jpg")],
            rel => have.TryGetValue(rel, out var s) ? s : null);
        Assert.Equal(["/sdcard/DCIM/b.jpg", "/sdcard/DCIM/c.jpg"], plan.Select(i => i.Path));
    }
}
