using Streamy.Core.Adb;

namespace Streamy.Core.Tests;

/// <summary>Output captured from a Galaxy A36 (Android 17), trimmed.</summary>
public class DeviceInfoTests
{
    private const string Output = """
        [ro.board.platform]: [parrot]
        [ro.build.display.id]: [CP2A.260605.016.A366BXXUCDZIF]
        [ro.build.version.release]: [17]
        [ro.build.version.sdk]: [37]
        [ro.build.version.security_patch]: [2026-09-05]
        [ro.hardware]: [qcom]
        [ro.product.cpu.abi]: [arm64-v8a]
        [ro.product.manufacturer]: [samsung]
        [ro.product.model]: [SM-A366B]
        [ro.soc.manufacturer]: [QTI]
        [ro.soc.model]: [SM6475]
        ---MEM
        MemTotal:        7529432 kB
        MemFree:          205876 kB
        MemAvailable:    3093704 kB
        ---DF
        Filesystem       1K-blocks      Used Available Use% Mounted on
        /dev/block/dm-96 232398848 148700876  83566900  65% /data/user/0
        ---GLES
        GLES: Qualcomm, Adreno (TM) 710, OpenGL ES 3.2 V@0800.80 (GIT@7853d15211, I1594d2c915, 1783661900) (Date:07/10/26)
        ---BAT
        Current Battery Service state:
          AC powered: false
          USB powered: true
          Wireless powered: false
          status: 5
          level: 100
          scale: 100
          temperature: 324
        ---WM
        Physical size: 1080x2340
        Physical density: 450
        ---CPU
        8
        1804800
        1804800
        2400000
        2400000
        ---END
        """;

    private static readonly DeviceInfo Info = DeviceInfo.Parse(Output);

    [Fact]
    public void System()
    {
        Assert.Equal("Samsung SM-A366B", Info.Name);
        Assert.Equal("Android 17 (API 37), security patch 2026-09-05", Info.AndroidText);
    }

    [Fact]
    public void Chip()
    {
        Assert.Equal("Qualcomm SM6475, 8 cores, up to 2.40 GHz, arm64-v8a", Info.CpuText);
        Assert.Equal("Adreno 710 (OpenGL ES 3.2)", Info.GpuText);
    }

    [Fact]
    public void MemoryAndStorage()
    {
        Assert.Equal("7.2 GB total, 3.0 GB available", Info.RamText);
        Assert.Equal("142 GB used of 222 GB, 79.7 GB free", Info.StorageText);
    }

    [Fact]
    public void BatteryAndScreen()
    {
        Assert.Equal("100%, Full, on USB, 32.4 °C", Info.BatteryText);
        Assert.Equal("1080 × 2340, 450 dpi", Info.ScreenText);
    }

    [Fact]
    public void MissingSectionsSayUnknown()
    {
        var empty = DeviceInfo.Parse("");
        Assert.Equal("Unknown", empty.RamText);
        Assert.Equal("Unknown", empty.GpuText);
        Assert.Equal("Unknown", empty.AndroidText);
    }

    [Fact]
    public void MediaTekChip()
    {
        var info = DeviceInfo.Parse("[ro.soc.manufacturer]: [Mediatek]\n[ro.soc.model]: [MT6789V/CD]\n---CPU\n8\n2200000\n");
        Assert.Equal("MediaTek MT6789V/CD, 8 cores, up to 2.20 GHz", info.CpuText);
    }
}
