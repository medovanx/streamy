using System.Text.RegularExpressions;

namespace Streamy.Core.Adb;

/// <summary>One line of "adb devices -l".</summary>
public sealed record AdbDevice(string Serial, string State, string? Model, string? Product, string? Device, string? TransportId)
{
    /// <summary>"device" means authorized and ready; others are "unauthorized", "offline", "recovery"...</summary>
    public bool IsReady => State == "device";

    /// <summary>Connected over TCP/IP: an "ip:port" serial or an mDNS name from wireless debugging.</summary>
    public bool IsWireless => Serial.Contains(':') || Serial.StartsWith("adb-", StringComparison.Ordinal);

    /// <summary>Model with underscores turned into spaces (adb replaces spaces), or the serial.</summary>
    public string DisplayName => Model?.Replace('_', ' ') ?? Serial;
}

/// <summary>A device advertised by Android's wireless debugging over mDNS ("adb mdns services").</summary>
public sealed record AdbMdnsService(string Name, string Type, string Address)
{
    public bool IsPairing => Type.StartsWith("_adb-tls-pairing", StringComparison.Ordinal);
    public bool IsConnect => Type.StartsWith("_adb-tls-connect", StringComparison.Ordinal);
}

public static partial class AdbParsers
{
    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in parts.Skip(2))
            {
                int colon = p.IndexOf(':');
                if (colon > 0) props[p[..colon]] = p[(colon + 1)..];
            }
            list.Add(new AdbDevice(parts[0], parts[1],
                props.GetValueOrDefault("model"), props.GetValueOrDefault("product"),
                props.GetValueOrDefault("device"), props.GetValueOrDefault("transport_id")));
        }
        return list;
    }

    public static IReadOnlyList<AdbMdnsService> ParseMdnsServices(string output)
    {
        var list = new List<AdbMdnsService>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[1].StartsWith("_adb", StringComparison.Ordinal) && parts[2].Contains(':'))
                list.Add(new AdbMdnsService(parts[0], parts[1].TrimEnd('.'), parts[2]));
        }
        return list;
    }

    /// <summary>"adb pair HOST:PORT CODE" succeeded.</summary>
    public static bool IsPairSuccess(string output) => output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);

    /// <summary>"adb connect HOST:PORT" succeeded (newly or already connected).</summary>
    public static bool IsConnectSuccess(string output) =>
        ConnectedRegex().IsMatch(output) && !output.Contains("failed", StringComparison.OrdinalIgnoreCase) && !output.Contains("cannot", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses "host:port", accepting IPv4, names and bracketed IPv6. Port defaults to 5555.</summary>
    public static bool TryParseEndpoint(string text, out string host, out int port)
    {
        host = "";
        port = 5555;
        text = text.Trim();
        if (text.Length == 0) return false;

        string portPart = "";
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0) return false;
            host = text[1..close];
            if (close + 1 < text.Length)
            {
                if (text[close + 1] != ':') return false;
                portPart = text[(close + 2)..];
            }
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon >= 0 && text.IndexOf(':') == colon) { host = text[..colon]; portPart = text[(colon + 1)..]; }
            else if (colon < 0) host = text;
            else return false; // bare IPv6 without brackets is ambiguous
        }

        if (host.Length == 0) return false;
        if (portPart.Length > 0 && (!int.TryParse(portPart, out port) || port is < 1 or > 65535)) return false;
        return true;
    }

    /// <summary>The phone's Wi-Fi IPv4 address from "ip -f inet addr show wlan0", or null.</summary>
    public static string? ParseWlanAddress(string output)
    {
        var m = InetRegex().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"adb install" printed Success.</summary>
    public static bool IsInstallSuccess(string output) =>
        output.Split('\n').Any(l => l.Trim().Equals("Success", StringComparison.Ordinal));

    /// <summary>The reason "adb install" gives, e.g. INSTALL_FAILED_VERSION_DOWNGRADE.</summary>
    public static string? InstallFailure(string output)
    {
        var m = FailureRegex().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"inet (\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})/")]
    private static partial Regex InetRegex();

    [GeneratedRegex(@"Failure \[([A-Z_]+)")]
    private static partial Regex FailureRegex();

    [GeneratedRegex(@"\bconnected to\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectedRegex();
}
