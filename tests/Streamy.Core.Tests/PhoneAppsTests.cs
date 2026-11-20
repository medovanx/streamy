using Streamy.Core.Apps;

namespace Streamy.Core.Tests;

/// <summary>The helper's output (made-up apps).</summary>
public class PhoneAppsTests
{
    private const string Output =
        "com.example.chat\tChat\t2.1.0\t210\tl\t1700000000000\t1760000000000\t50000000\t60000000\t200000000\t10000000\n" +
        "com.android.settings\tSettings\t17\t37\tsl\t1000\t2000\t30000000\t-1\t-1\t-1\r\n" +
        "com.example.tabs\tTabs\tbad\tthing\t1.0\t1\td\t5\t5\t100\t1\t2\t3\n" +
        "garbage line\n" +
        "com.example.nolabel\t\t\t1\t\t0\t0\t10\t-1\t-1\t-1\n";

    private static readonly IReadOnlyList<PhoneApp> Apps = PhoneApps.Parse(Output);

    [Fact]
    public void ParsesFieldsAndSortsByLabel()
    {
        Assert.Equal(["com.example.chat", "com.example.nolabel", "com.android.settings", "com.example.tabs"], Apps.Select(a => a.Package));
        var chat = Apps[0];
        Assert.Equal("Chat", chat.Label);
        Assert.Equal("2.1.0", chat.VersionName);
        Assert.Equal(210, chat.VersionCode);
        Assert.True(chat.IsLaunchable);
        Assert.False(chat.IsSystem);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000), chat.Updated);
        Assert.Equal(270_000_000, chat.TotalBytes);
        Assert.Equal("com.example.chat_210", chat.IconKey);
    }

    [Fact]
    public void UnknownSizesFallBackToTheApk()
    {
        var settings = Apps.Single(a => a.Package == "com.android.settings");
        Assert.True(settings.IsSystem);
        Assert.Equal(30_000_000, settings.TotalBytes);
    }

    [Fact]
    public void MissingLabelUsesThePackage() =>
        Assert.Equal("com.example.nolabel", Apps.Single(a => a.Package == "com.example.nolabel").Label);

    [Fact]
    public void IconsSkipCutLinesAndKeepFailures()
    {
        var png = Convert.ToBase64String([1, 2, 3]);
        var icons = PhoneApps.ParseIcons($"a.b\t{png}\nc.d\t-\ne.f\t{png[..3]}").ToList();
        Assert.Equal(2, icons.Count);
        Assert.Equal([1, 2, 3], icons[0].Png!);
        Assert.Null(icons[1].Png);
    }

    [Fact]
    public void CommandsQuotePackages()
    {
        Assert.Equal("CLASSPATH=/data/local/tmp/streamy-helper.jar app_process / dev.streamy.helper.Main icons 96 'a.b' 'c.d'",
            PhoneApps.IconsCommand(["a.b", "c.d"]));
        Assert.Equal("monkey -p 'a.b' -c android.intent.category.LAUNCHER 1", PhoneApps.LaunchCommand("a.b"));
        Assert.Equal("pm clear 'a.b'", PhoneApps.ClearDataCommand("a.b"));
    }

    [Fact]
    public void PathsListBaseAndSplits() =>
        Assert.Equal(["/data/app/x/base.apk", "/data/app/x/split_config.arm64_v8a.apk"],
            PhoneApps.ParsePaths("package:/data/app/x/base.apk\r\npackage:/data/app/x/split_config.arm64_v8a.apk\n"));

    [Fact]
    public void BackupNameIsFileSafe() =>
        Assert.Equal("Chat_ Pro 2.1.0", PhoneApps.BackupName(Apps[0] with { Label = "Chat: Pro" }));
}
