using Streamy.Core.Adb;

namespace Streamy.Core.Tests;

public class AdbParserTests
{
    [Fact]
    public void ParsesUsbAndWirelessDevices()
    {
        const string output = """
            * daemon started successfully
            List of devices attached
            RKGL2002JQN            device product:a36xqnaxx model:SM_A366B device:a36xq transport_id:3
            192.168.1.20:41235     device product:panther model:Pixel_7 device:panther transport_id:5
            adb-R58M12345-AbCdEf._adb-tls-connect._tcp device product:x model:Galaxy_S21 device:o1s transport_id:6
            emulator-5554          unauthorized transport_id:7

            """;
        var devices = AdbParsers.ParseDevices(output);

        Assert.Equal(4, devices.Count);
        Assert.Equal("RKGL2002JQN", devices[0].Serial);
        Assert.Equal("SM A366B", devices[0].DisplayName);
        Assert.True(devices[0].IsReady);
        Assert.False(devices[0].IsWireless);
        Assert.True(devices[1].IsWireless);
        Assert.True(devices[2].IsWireless);
        Assert.False(devices[3].IsReady);
        Assert.Equal("emulator-5554", devices[3].DisplayName);
    }

    [Fact]
    public void EmptyListHasNoDevices() => Assert.Empty(AdbParsers.ParseDevices("List of devices attached\r\n\r\n"));

    [Fact]
    public void ParsesMdnsServices()
    {
        const string output = """
            List of discovered mdns services
            adb-RKGL2002JQN-Xy12ab	_adb-tls-connect._tcp	192.168.1.20:41235
            adb-RKGL2002JQN-Xy12ab	_adb-tls-pairing._tcp	192.168.1.20:37011
            """;
        var services = AdbParsers.ParseMdnsServices(output);
        Assert.Equal(2, services.Count);
        Assert.True(services[0].IsConnect);
        Assert.True(services[1].IsPairing);
        Assert.Equal("192.168.1.20:37011", services[1].Address);
    }

    [Theory]
    [InlineData("Successfully paired to 192.168.1.20:37011 [guid=adb-RKGL2002JQN-Xy12ab]", true)]
    [InlineData("Failed: Unable to start pairing client.", false)]
    public void Pair(string output, bool ok) => Assert.Equal(ok, AdbParsers.IsPairSuccess(output));

    [Theory]
    [InlineData("connected to 192.168.1.20:5555", true)]
    [InlineData("already connected to 192.168.1.20:5555", true)]
    [InlineData("failed to connect to '192.168.1.20:5555': Connection refused", false)]
    [InlineData("cannot connect to 192.168.1.20:5555: No route to host", false)]
    public void Connect(string output, bool ok) => Assert.Equal(ok, AdbParsers.IsConnectSuccess(output));

    [Theory]
    [InlineData("192.168.1.20:41235", "192.168.1.20", 41235)]
    [InlineData("192.168.1.20", "192.168.1.20", 5555)]
    [InlineData("phone.local:5555", "phone.local", 5555)]
    [InlineData("[fe80::1]:5555", "fe80::1", 5555)]
    public void Endpoint(string text, string host, int port)
    {
        Assert.True(AdbParsers.TryParseEndpoint(text, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.20:99999")]
    [InlineData("fe80::1")]
    [InlineData(":5555")]
    public void BadEndpoint(string text) => Assert.False(AdbParsers.TryParseEndpoint(text, out _, out _));
}
