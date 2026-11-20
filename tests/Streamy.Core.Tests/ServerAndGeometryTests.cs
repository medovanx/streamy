using Streamy.Core.Geometry;
using Streamy.Core.Input;
using Streamy.Core.Protocol;
using Streamy.Core.Server;

namespace Streamy.Core.Tests;

public class ServerOptionsTests
{
    [Fact]
    public void DefaultsWriteOnlyScidLogLevelAndForwardTunnel()
    {
        var o = new ServerOptions { Scid = 0x12345678 };
        Assert.Equal(["scid=12345678", "log_level=info", "tunnel_forward=true"], o.ToArguments());
        Assert.Equal("scrcpy_12345678", o.SocketName);
    }

    [Fact]
    public void AppWindowsAskForANewDisplayAndStartTheApp()
    {
        var o = new ServerOptions { Scid = 1, NewDisplay = "", StartApp = "com.example.chat" };
        Assert.Equal(["new_display=", "start_app=com.example.chat"], o.ToArguments().TakeLast(2));
        Assert.DoesNotContain(new ServerOptions { Scid = 1 }.ToArguments(), a => a.StartsWith("new_display", StringComparison.Ordinal));
    }

    [Fact]
    public void ScidIsZeroPaddedHex()
    {
        var o = new ServerOptions { Scid = 0xAB };
        Assert.Equal("scid=000000ab", o.ToArguments()[0]);
        Assert.Equal("scrcpy_000000ab", o.SocketName);
    }

    [Fact]
    public void ShellCommandStartsTheServerWithThePinnedVersion()
    {
        var o = new ServerOptions { Scid = 1, Audio = false, MaxSize = 1920, VideoCodec = "h265", StayAwake = true };
        var cmd = o.ToShellCommand();
        Assert.Equal(
        [
            "CLASSPATH=/data/local/tmp/scrcpy-server.jar", "app_process", "/", "com.genymobile.scrcpy.Server", ScrcpyProtocol.ServerVersion,
            "scid=00000001", "log_level=info", "audio=false", "video_codec=h265", "max_size=1920", "tunnel_forward=true", "stay_awake=true",
        ], cmd);
    }

    [Fact]
    public void NewScidIs31Bit()
    {
        for (int i = 0; i < 100; i++) Assert.True(ServerOptions.NewScid() <= 0x7FFFFFFF);
    }
}

public class ViewMapperTests
{
    [Fact]
    public void PortraitFrameInWideWindowIsPillarboxed()
    {
        var m = new ViewMapper(1000, 1000, 1080, 2400);
        var (x, y, w, h) = m.ContentRect;
        Assert.Equal((275, 0, 450, 1000), (x, y, w, h));
        Assert.Equal(new DevicePosition(540, 1200, 1080, 2400), m.Map(500, 500));
        Assert.False(m.Contains(100, 500));
    }

    [Fact]
    public void PointsInTheBarsClampToTheEdge()
    {
        var m = new ViewMapper(1000, 1000, 1080, 2400);
        Assert.Equal(0, m.Map(0, 500).X);
        Assert.Equal(1079, m.Map(999, 500).X);
    }

    [Fact]
    public void LandscapeFrameInTallWindowIsLetterboxed()
    {
        var m = new ViewMapper(500, 1000, 2400, 1080);
        var (_, y, w, h) = m.ContentRect;
        Assert.Equal(500, w);
        Assert.Equal(225, h);
        Assert.Equal(387, y);
    }
}

public class KeyMapTests
{
    [Theory]
    [InlineData(0x41, AndroidKeycode.A)]
    [InlineData(0x5A, AndroidKeycode.Z)]
    [InlineData(0x30, AndroidKeycode.Num0)]
    [InlineData(0x39, AndroidKeycode.Num9)]
    [InlineData(0x70, AndroidKeycode.F1)]
    [InlineData(0x7B, AndroidKeycode.F12)]
    [InlineData(0x0D, AndroidKeycode.Enter)]
    [InlineData(0x08, AndroidKeycode.Del)]
    [InlineData(0x2E, AndroidKeycode.ForwardDel)]
    [InlineData(0x26, AndroidKeycode.DpadUp)]
    [InlineData(0x91, AndroidKeycode.Unknown)] // Scroll Lock
    public void MapsVirtualKeys(int vk, AndroidKeycode expected) => Assert.Equal(expected, KeyMap.FromVirtualKey(vk));

    [Fact]
    public void LettersAreText() => Assert.False(KeyMap.IsNonText(0x41));

    [Fact]
    public void ArrowsAreNotText() => Assert.True(KeyMap.IsNonText(0x25));
}
