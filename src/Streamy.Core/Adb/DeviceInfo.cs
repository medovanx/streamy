using System.Globalization;
using System.Text.RegularExpressions;

namespace Streamy.Core.Adb;

/// <summary>What Streamy shows about a phone: system, chip, memory, storage, battery and screen.</summary>
public sealed partial record DeviceInfo
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string? MarketName { get; init; }
    public string AndroidVersion { get; init; } = "";
    public int Sdk { get; init; }
    public string? SecurityPatch { get; init; }
    public string? Build { get; init; }

    public string? Soc { get; init; }
    public int Cores { get; init; }
    public double MaxGhz { get; init; }
    public string? Abi { get; init; }
    public string? Gpu { get; init; }
    public string? GpuApi { get; init; }

    public long RamTotalKb { get; init; }
    public long RamAvailableKb { get; init; }
    public long StorageTotalKb { get; init; }
    public long StorageUsedKb { get; init; }
    public long StorageFreeKb { get; init; }

    public int? BatteryLevel { get; init; }
    public double? BatteryTempC { get; init; }
    public string? BatteryStatus { get; init; }
    public string? PoweredBy { get; init; }

    public string? ScreenSize { get; init; }
    public int? Density { get; init; }

    /// <summary>The shell script whose output <see cref="Parse"/> reads: one adb call for everything.</summary>
    public const string Script =
        "getprop; echo ---MEM; cat /proc/meminfo; echo ---DF; df -k /data; " +
        "echo ---GLES; dumpsys SurfaceFlinger | grep -m1 'GLES:'; echo ---BAT; dumpsys battery; " +
        "echo ---WM; wm size; wm density; echo ---CPU; nproc; cat /sys/devices/system/cpu/cpu*/cpufreq/cpuinfo_max_freq; echo ---END";

    /// <summary>The changing part only (battery, storage, memory), for refreshing a phone window's title.</summary>
    public const string StatsScript = "echo ---MEM; cat /proc/meminfo; echo ---DF; df -k /data; echo ---BAT; dumpsys battery; echo ---END";

    /// <summary>"87% charging, 31 °C" for a window title; null when the battery is unknown.</summary>
    public string? ShortBattery => BatteryLevel is not { } level ? null
        : $"{level}%{(BatteryStatus is "Charging" ? " charging" : BatteryStatus is "Full" ? " full" : "")}"
          + (BatteryTempC is { } c ? ", " + c.ToString("0", CultureInfo.InvariantCulture) + " °C" : "");

    public static DeviceInfo Parse(string output)
    {
        var sections = SplitSections(output);
        var props = ParseGetprop(sections.GetValueOrDefault(""));
        string P(string key) => props.GetValueOrDefault(key, "");

        var mem = KeyValues(sections.GetValueOrDefault("MEM"), ':');
        long Kb(string key) => mem.TryGetValue(key, out var v) && long.TryParse(v.Split(' ')[0], out var n) ? n : 0;

        var (total, used, free) = ParseDf(sections.GetValueOrDefault("DF"));
        var (gpu, gpuApi) = ParseGles(sections.GetValueOrDefault("GLES"));
        var bat = KeyValues(sections.GetValueOrDefault("BAT"), ':');
        var (cores, ghz) = ParseCpu(sections.GetValueOrDefault("CPU"));
        var wm = KeyValues(sections.GetValueOrDefault("WM"), ':');

        string socMaker = P("ro.soc.manufacturer") switch
        {
            "QTI" or "Qualcomm" => "Qualcomm",
            "Mediatek" or "MediaTek" => "MediaTek",
            var m => m,
        };
        string socModel = P("ro.soc.model");
        string? soc = socModel.Length > 0 ? $"{socMaker} {socModel}".Trim()
            : P("ro.board.platform") is { Length: > 0 } platform ? platform : null;

        int? level = int.TryParse(bat.GetValueOrDefault("level"), out var l) ? l : null;
        double? temp = int.TryParse(bat.GetValueOrDefault("temperature"), out var t) ? t / 10.0 : null;
        string? status = bat.GetValueOrDefault("status") switch
        {
            "2" => "Charging",
            "3" => "Discharging",
            "4" => "Not charging",
            "5" => "Full",
            _ => null,
        };
        string? powered = bat.GetValueOrDefault("AC powered") == "true" ? "Charger"
            : bat.GetValueOrDefault("USB powered") == "true" ? "USB"
            : bat.GetValueOrDefault("Wireless powered") == "true" ? "Wireless charger"
            : null;

        return new DeviceInfo
        {
            Manufacturer = Capitalize(P("ro.product.manufacturer")),
            Model = P("ro.product.model"),
            MarketName = NullIfEmpty(P("ro.product.marketname")) ?? NullIfEmpty(P("ro.product.vendor.marketname")),
            AndroidVersion = P("ro.build.version.release"),
            Sdk = int.TryParse(P("ro.build.version.sdk"), out var sdk) ? sdk : 0,
            SecurityPatch = NullIfEmpty(P("ro.build.version.security_patch")),
            Build = NullIfEmpty(P("ro.build.display.id")),
            Soc = soc,
            Cores = cores,
            MaxGhz = ghz,
            Abi = NullIfEmpty(P("ro.product.cpu.abi")),
            Gpu = gpu,
            GpuApi = gpuApi,
            RamTotalKb = Kb("MemTotal"),
            RamAvailableKb = Kb("MemAvailable"),
            StorageTotalKb = total,
            StorageUsedKb = used,
            StorageFreeKb = free,
            BatteryLevel = level,
            BatteryTempC = temp,
            BatteryStatus = status,
            PoweredBy = powered,
            ScreenSize = wm.GetValueOrDefault("Override size") ?? wm.GetValueOrDefault("Physical size"),
            Density = int.TryParse(wm.GetValueOrDefault("Override density") ?? wm.GetValueOrDefault("Physical density"), out var d) ? d : null,
        };
    }

    // ---- Display text ----

    public string Name => MarketName ?? $"{Manufacturer} {Model}".Trim();

    public string AndroidText => AndroidVersion.Length == 0 ? "Unknown"
        : $"Android {AndroidVersion}" + (Sdk > 0 ? $" (API {Sdk})" : "") + (SecurityPatch is null ? "" : $", security patch {SecurityPatch}");

    public string CpuText => string.Join(", ", new[]
    {
        Soc,
        Cores > 0 ? $"{Cores} cores" : null,
        MaxGhz > 0 ? $"up to {MaxGhz.ToString("0.00", CultureInfo.InvariantCulture)} GHz" : null,
        Abi,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string GpuText => Gpu is null ? "Unknown" : GpuApi is null ? Gpu : $"{Gpu} ({GpuApi})";

    public string RamText => RamTotalKb == 0 ? "Unknown" : $"{Gb(RamTotalKb)} total, {Gb(RamAvailableKb)} available";

    public string StorageText => StorageTotalKb == 0 ? "Unknown"
        : $"{Gb(StorageUsedKb)} used of {Gb(StorageTotalKb)}, {Gb(StorageFreeKb)} free";

    public string BatteryText => BatteryLevel is null ? "Unknown" : string.Join(", ", new[]
    {
        $"{BatteryLevel}%",
        BatteryStatus,
        PoweredBy is null ? null : $"on {PoweredBy}",
        BatteryTempC is { } c ? c.ToString("0.0", CultureInfo.InvariantCulture) + " °C" : null,
    }.Where(s => s is not null));

    public string ScreenText => ScreenSize is null ? "Unknown" : ScreenSize.Replace("x", " × ") + (Density is { } dpi ? $", {dpi} dpi" : "");

    /// <summary>Kilobytes as GB with one decimal (1 GB = 1024³ bytes, as Android's own Settings counts RAM).</summary>
    public static string Gb(long kb) => (kb / 1024.0 / 1024.0).ToString(kb >= 100L * 1024 * 1024 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";

    // ---- Parsing helpers ----

    private static Dictionary<string, string> SplitSections(string output)
    {
        var result = new Dictionary<string, string>();
        string current = "";
        var sb = new System.Text.StringBuilder();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("---", StringComparison.Ordinal))
            {
                result[current] = sb.ToString();
                sb.Clear();
                current = raw[3..].Trim();
                continue;
            }
            sb.AppendLine(raw);
        }
        result[current] = sb.ToString();
        return result;
    }

    public static Dictionary<string, string> ParseGetprop(string? text)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null) return props;
        foreach (Match m in GetpropRegex().Matches(text)) props[m.Groups[1].Value] = m.Groups[2].Value;
        return props;
    }

    private static Dictionary<string, string> KeyValues(string? text, char sep)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null) return d;
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf(sep);
            if (i <= 0) continue;
            var key = line[..i].Trim();
            if (!d.ContainsKey(key)) d[key] = line[(i + 1)..].Trim();
        }
        return d;
    }

    /// <summary>The first data line of "df -k": size, used, available in KB.</summary>
    public static (long Total, long Used, long Free) ParseDf(string? text)
    {
        if (text is null) return default;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && long.TryParse(parts[1], out var total) && long.TryParse(parts[2], out var used) && long.TryParse(parts[3], out var free))
                return (total, used, free);
        }
        return default;
    }

    /// <summary>"GLES: Qualcomm, Adreno (TM) 710, OpenGL ES 3.2 V@..." → ("Adreno 710", "OpenGL ES 3.2").</summary>
    public static (string? Gpu, string? Api) ParseGles(string? text)
    {
        var line = text?.Split('\n').FirstOrDefault(l => l.Contains("GLES:", StringComparison.Ordinal));
        if (line is null) return (null, null);
        var parts = line[(line.IndexOf("GLES:", StringComparison.Ordinal) + 5)..].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return (null, null);
        string gpu = parts[1].Replace("(TM)", "").Replace("  ", " ").Trim();
        string? api = parts.Length > 2 ? ApiRegex().Match(parts[2]) is { Success: true } m ? m.Value : null : null;
        return (gpu, api);
    }

    /// <summary>"nproc" then one cpuinfo_max_freq per core (kHz).</summary>
    public static (int Cores, double MaxGhz) ParseCpu(string? text)
    {
        if (text is null) return default;
        var numbers = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => long.TryParse(l, out var n) ? n : -1).Where(n => n > 0).ToList();
        if (numbers.Count == 0) return default;
        int cores = (int)numbers[0];
        long maxKhz = numbers.Skip(1).DefaultIfEmpty(0).Max();
        return (cores, maxKhz / 1_000_000.0);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    [GeneratedRegex(@"^\[([^\]]+)\]: \[(.*)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex GetpropRegex();

    [GeneratedRegex(@"OpenGL ES [\d.]+")]
    private static partial Regex ApiRegex();
}
