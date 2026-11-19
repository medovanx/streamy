using System.Security.Cryptography;

namespace Streamy.Core.Adb;

/// <summary>
/// The QR code Android's "Pair device with QR code" screen scans. Its text is
/// <c>WIFI:T:ADB;S:&lt;service name&gt;;P:&lt;password&gt;;;</c>. After scanning, the phone advertises an
/// <c>_adb-tls-pairing</c> mDNS service under that name, and the PC pairs with <c>adb pair address password</c>.
/// </summary>
public sealed record WirelessPairing(string ServiceName, string Password)
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no look-alikes (l, 1, o, 0)

    public static WirelessPairing Create() =>
        new("studio-" + Random(10), Random(12));

    public string QrText => $"WIFI:T:ADB;S:{ServiceName};P:{Password};;";

    /// <summary>The pairing service among discovered ones whose name is ours, or null while the phone has not scanned yet.</summary>
    public AdbMdnsService? FindService(IEnumerable<AdbMdnsService> services) =>
        services.FirstOrDefault(s => s.IsPairing && s.Name.Equals(ServiceName, StringComparison.Ordinal));

    private static string Random(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
}
