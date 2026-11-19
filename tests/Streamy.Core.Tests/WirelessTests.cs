using Streamy.Core.Adb;

namespace Streamy.Core.Tests;

public class WirelessTests
{
    [Fact]
    public void ParsesTheWifiAddress()
    {
        const string output = """
            44: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default qlen 3000
                inet 192.168.1.28/24 brd 192.168.1.255 scope global dynamic wlan0
                   valid_lft 77429sec preferred_lft 77429sec
            """;
        Assert.Equal("192.168.1.28", AdbParsers.ParseWlanAddress(output));
    }

    [Fact]
    public void NoAddressWhenWifiIsOff() =>
        Assert.Null(AdbParsers.ParseWlanAddress("Device \"wlan0\" does not exist."));

    [Fact]
    public void InstallResults()
    {
        Assert.True(AdbParsers.IsInstallSuccess("Performing Streamed Install\r\nSuccess\r\n"));
        const string failed = "Performing Streamed Install\nadb: failed to install app.apk: Failure [INSTALL_FAILED_VERSION_DOWNGRADE: Downgrade detected]";
        Assert.False(AdbParsers.IsInstallSuccess(failed));
        Assert.Equal("INSTALL_FAILED_VERSION_DOWNGRADE", AdbParsers.InstallFailure(failed));
    }

    [Fact]
    public void QrTextHasAndroidsFormat()
    {
        var p = WirelessPairing.Create();
        Assert.StartsWith("studio-", p.ServiceName);
        Assert.Equal(12, p.Password.Length);
        Assert.Equal($"WIFI:T:ADB;S:{p.ServiceName};P:{p.Password};;", p.QrText);
        Assert.NotEqual(p.Password, WirelessPairing.Create().Password);
    }

    [Fact]
    public void FindsOurPairingServiceOnly()
    {
        var p = new WirelessPairing("studio-abc", "pw");
        var services = new[]
        {
            new AdbMdnsService("adb-R58N12ABCDE-x", "_adb-tls-connect._tcp", "192.168.1.28:41235"),
            new AdbMdnsService("studio-other", "_adb-tls-pairing._tcp", "192.168.1.30:37000"),
            new AdbMdnsService("studio-abc", "_adb-tls-pairing._tcp", "192.168.1.28:37011"),
        };
        Assert.Equal("192.168.1.28:37011", p.FindService(services)?.Address);
        Assert.Null(p.FindService(services[..2]));
    }
}
