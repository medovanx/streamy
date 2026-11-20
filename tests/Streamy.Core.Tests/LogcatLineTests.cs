using Streamy.Core.Adb;

namespace Streamy.Core.Tests;

public class LogcatLineTests
{
    [Fact]
    public void ParsesThreadtime()
    {
        var l = LogcatLine.Parse("11-20 06:55:59.781 15860 15869 E AndroidRuntime: FATAL EXCEPTION: main")!;
        Assert.Equal("11-20 06:55:59.781", l.Time);
        Assert.Equal(15860, l.Pid);
        Assert.Equal(15869, l.Tid);
        Assert.Equal('E', l.Level);
        Assert.Equal("AndroidRuntime", l.Tag);
        Assert.Equal("FATAL EXCEPTION: main", l.Message);
        Assert.Equal(4, l.Severity);
    }

    [Fact]
    public void TagsWithSpacesAndPaddedPids()
    {
        var l = LogcatLine.Parse("11-20 06:55:59.781   901  1022 I Some Tag  : hello: world")!;
        Assert.Equal(901, l.Pid);
        Assert.Equal("Some Tag", l.Tag);
        Assert.Equal("hello: world", l.Message);
    }

    [Fact]
    public void EmptyMessage() => Assert.Equal("", LogcatLine.Parse("11-20 06:55:59.781 1 2 D Tag:")!.Message);

    [Fact]
    public void BannersAreNotEntries() => Assert.Null(LogcatLine.Parse("--------- beginning of main"));

    [Fact]
    public void FilterMatchesTagMessageOrPid()
    {
        var l = LogcatLine.Parse("11-20 06:55:59.781 15860 15869 W ActivityManager: Slow operation")!;
        Assert.True(l.Matches("activitymanager"));
        Assert.True(l.Matches("SLOW"));
        Assert.True(l.Matches("15860"));
        Assert.False(l.Matches("1586"));
        Assert.False(l.Matches("camera"));
    }
}
