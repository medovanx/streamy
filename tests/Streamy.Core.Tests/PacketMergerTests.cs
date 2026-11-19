using Streamy.Core.Protocol;

namespace Streamy.Core.Tests;

public class PacketMergerTests
{
    private static readonly PacketHeader Config = new() { IsConfig = true, PayloadSize = 2 };
    private static readonly PacketHeader Media = new() { IsKeyFrame = true, PayloadSize = 3 };

    [Fact]
    public void ConfigIsHeldAndPrependedOnce()
    {
        var m = new PacketMerger();
        Assert.Null(m.Merge(Config, [1, 2]));
        Assert.Equal(new byte[] { 1, 2, 7, 8, 9 }, m.Merge(Media, [7, 8, 9]));
        Assert.Equal(new byte[] { 4, 5, 6 }, m.Merge(Media, [4, 5, 6]));
    }

    [Fact]
    public void NewerConfigReplacesTheOlderOne()
    {
        var m = new PacketMerger();
        m.Merge(Config, [1, 1]);
        m.Merge(Config, [2, 2]);
        Assert.Equal(new byte[] { 2, 2, 9 }, m.Merge(Media, [9]));
    }

    [Fact]
    public void MediaWithoutConfigPassesThrough() => Assert.Equal(new byte[] { 3 }, new PacketMerger().Merge(Media, [3]));
}
