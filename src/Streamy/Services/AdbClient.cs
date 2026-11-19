using System.Diagnostics;
using System.Globalization;
using System.Text;
using Streamy.Core.Adb;
using Streamy.Core.Files;

namespace Streamy.Services;

public sealed record AdbResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string All => (Output + "\n" + Error).Trim();
}

/// <summary>Runs the bundled adb.exe. Arguments go through ArgumentList, so nothing is parsed by a shell on the PC.</summary>
public sealed class AdbClient(string adbPath)
{
    public static AdbClient Default { get; } = new(Dependencies.AdbPath);

    public async Task<AdbResult> RunAsync(IEnumerable<string> args, string? serial = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = CreateStartInfo(args, serial) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("adb " + string.Join(' ', args) + " did not finish in time");
        }
        return new AdbResult(p.ExitCode, await stdout, await stderr);
    }

    /// <summary>Starts a long-running adb command (the server shell) and returns the process.</summary>
    public Process Start(IEnumerable<string> args, string? serial = null)
    {
        var p = new Process { StartInfo = CreateStartInfo(args, serial), EnableRaisingEvents = true };
        p.Start();
        return p;
    }

    public async Task<IReadOnlyList<AdbDevice>> DevicesAsync(CancellationToken ct = default) =>
        AdbParsers.ParseDevices((await RunAsync(["devices", "-l"], timeout: TimeSpan.FromSeconds(10), ct: ct)).Output);

    /// <summary>Wireless debugging services seen on the network. Kept short: a stuck mDNS query holds up the adb server.</summary>
    public async Task<IReadOnlyList<AdbMdnsService>> MdnsServicesAsync(CancellationToken ct = default)
    {
        try
        {
            return AdbParsers.ParseMdnsServices((await RunAsync(["mdns", "services"], timeout: TimeSpan.FromSeconds(5), ct: ct)).Output);
        }
        catch (TimeoutException)
        {
            return [];
        }
    }

    public async Task<AdbResult> PairAsync(string endpoint, string code, CancellationToken ct = default) =>
        await RunAsync(["pair", endpoint, code], timeout: TimeSpan.FromSeconds(20), ct: ct);

    public async Task<AdbResult> ConnectAsync(string endpoint, CancellationToken ct = default) =>
        await RunAsync(["connect", endpoint], timeout: TimeSpan.FromSeconds(15), ct: ct);

    public async Task<AdbResult> DisconnectAsync(string endpoint, CancellationToken ct = default) =>
        await RunAsync(["disconnect", endpoint], ct: ct);

    public async Task PushAsync(string serial, string local, string remote, CancellationToken ct = default)
    {
        var r = await RunAsync(["push", local, remote], serial, TimeSpan.FromMinutes(10), ct);
        if (!r.Ok) throw new IOException("adb push failed: " + r.All);
    }

    /// <summary>Installs (or updates, keeping data) an APK. Returns adb's output.</summary>
    public async Task<AdbResult> InstallAsync(string serial, string apk, CancellationToken ct = default) =>
        await RunAsync(["install", "-r", "-d", apk], serial, TimeSpan.FromMinutes(10), ct);

    public async Task<AdbResult> ShellAsync(string serial, IEnumerable<string> command, CancellationToken ct = default) =>
        await RunAsync(["shell", .. command], serial, ct: ct);

    // ---- Files ----

    /// <summary>Lists a phone folder. Throws <see cref="IOException"/> with the phone's message when it cannot be read.</summary>
    public async Task<IReadOnlyList<PhoneFile>> ListAsync(string serial, string path, CancellationToken ct = default)
    {
        var r = await RunAsync(["shell", PhoneFiles.ListCommand(path)], serial, TimeSpan.FromSeconds(30), ct);
        if (r.Error.Trim() is { Length: > 0 } err && r.Output.Trim().Length == 0)
            throw new IOException(CleanShellError(err));
        return PhoneFiles.ParseListing(r.Output);
    }

    /// <summary>Copies a phone file or folder into a PC folder.</summary>
    public async Task PullAsync(string serial, string remote, string localFolder, CancellationToken ct = default)
    {
        var r = await RunAsync(["pull", "-a", remote, localFolder], serial, TimeSpan.FromHours(2), ct);
        if (!r.Ok) throw new IOException(CleanShellError(r.All));
    }

    /// <summary>Copies a PC file or folder into a phone folder.</summary>
    public async Task PushIntoAsync(string serial, string local, string remoteFolder, CancellationToken ct = default)
    {
        var r = await RunAsync(["push", local, PhoneFiles.Combine(remoteFolder, Path.GetFileName(local.TrimEnd('\\', '/')))], serial, TimeSpan.FromHours(2), ct);
        if (!r.Ok) throw new IOException(CleanShellError(r.All));
    }

    public Task DeleteAsync(string serial, IEnumerable<string> paths, CancellationToken ct = default) =>
        ShellChecked(serial, "rm -rf -- " + string.Join(' ', paths.Select(PhoneFiles.Quote)), ct);

    public Task MakeFolderAsync(string serial, string path, CancellationToken ct = default) =>
        ShellChecked(serial, "mkdir -p -- " + PhoneFiles.Quote(path), ct);

    /// <summary>Renames without overwriting: fails if the new name is taken.</summary>
    public Task RenameAsync(string serial, string from, string to, CancellationToken ct = default) =>
        ShellChecked(serial, $"if [ -e {PhoneFiles.Quote(to)} ]; then echo 'That name is already taken.' >&2; exit 1; fi; mv -- {PhoneFiles.Quote(from)} {PhoneFiles.Quote(to)}", ct);

    private async Task ShellChecked(string serial, string command, CancellationToken ct)
    {
        var r = await RunAsync(["shell", command], serial, TimeSpan.FromMinutes(5), ct);
        if (!r.Ok) throw new IOException(CleanShellError(r.All));
    }

    /// <summary>"cd: /data: Permission denied" → "Permission denied"; adb's own prefixes removed.</summary>
    private static string CleanShellError(string text)
    {
        var line = text.Trim().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "The phone refused.";
        if (line.StartsWith("adb: error: ", StringComparison.Ordinal)) line = line["adb: error: ".Length..];
        int colon = line.LastIndexOf(": ", StringComparison.Ordinal);
        bool plain = line.Contains("denied", StringComparison.OrdinalIgnoreCase) || line.Contains("No such file", StringComparison.OrdinalIgnoreCase);
        return colon > 0 && plain ? line[(colon + 2)..] : line;
    }

    /// <summary>System, chip, memory, storage, battery and screen details, read in one shell call.</summary>
    public async Task<DeviceInfo> DeviceInfoAsync(string serial, CancellationToken ct = default) =>
        DeviceInfo.Parse((await RunAsync(["shell", DeviceInfo.Script], serial, TimeSpan.FromSeconds(15), ct)).Output);

    /// <summary>The phone's Wi-Fi address, or null when Wi-Fi is off.</summary>
    public async Task<string?> WifiAddressAsync(string serial, CancellationToken ct = default) =>
        AdbParsers.ParseWlanAddress((await ShellAsync(serial, ["ip", "-f", "inet", "addr", "show", "wlan0"], ct)).Output);

    /// <summary>Restarts adbd on the phone listening on TCP (works on every Android version, until the phone reboots).</summary>
    public async Task<AdbResult> TcpIpAsync(string serial, int port, CancellationToken ct = default) =>
        await RunAsync(["tcpip", port.ToString(CultureInfo.InvariantCulture)], serial, ct: ct);

    /// <summary>adb forward tcp:0 → the device socket; adb picks a free local port and prints it.</summary>
    public async Task<int> ForwardAsync(string serial, string deviceSocket, CancellationToken ct = default)
    {
        var r = await RunAsync(["forward", "tcp:0", "localabstract:" + deviceSocket], serial, ct: ct);
        if (!r.Ok || !int.TryParse(r.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            throw new IOException("adb forward failed: " + r.All);
        return port;
    }

    public async Task RemoveForwardAsync(string serial, int localPort) =>
        await RunAsync(["forward", "--remove", "tcp:" + localPort.ToString(CultureInfo.InvariantCulture)], serial);

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> args, string? serial)
    {
        var psi = new ProcessStartInfo(adbPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (serial is not null)
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
