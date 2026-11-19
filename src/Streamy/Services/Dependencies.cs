using System.Reflection;
using Streamy.Core.Protocol;

namespace Streamy.Services;

/// <summary>
/// Unpacks the embedded adb, scrcpy-server and FFmpeg files to %LOCALAPPDATA%\Streamy\bin\&lt;scrcpy version&gt;
/// the first time they are needed. A file is rewritten when its size differs, so a damaged copy repairs itself.
/// </summary>
public static class Dependencies
{
    private const string Prefix = "deps/";
    private static readonly Lock Gate = new();
    private static string? _dir;

    public static string Directory
    {
        get
        {
            lock (Gate) return _dir ??= Unpack();
        }
    }

    public static string AdbPath => Path.Combine(Directory, "adb.exe");
    public static string ServerPath => Path.Combine(Directory, "scrcpy-server");

    private static string Unpack()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Streamy", "bin", ScrcpyProtocol.ServerVersion);
        System.IO.Directory.CreateDirectory(dir);

        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        if (names.Count == 0)
            throw new FileNotFoundException("This build has no embedded adb or scrcpy-server. Run tools/fetch-deps.ps1 and rebuild.");

        foreach (var name in names)
        {
            using var src = asm.GetManifestResourceStream(name)!;
            var target = Path.Combine(dir, name[Prefix.Length..]);
            var info = new FileInfo(target);
            if (info.Exists && info.Length == src.Length) continue;

            // adb.exe may be running from an older unpack (the adb server outlives us); write beside it and swap.
            var tmp = target + ".tmp";
            using (var dst = File.Create(tmp)) src.CopyTo(dst);
            try
            {
                File.Move(tmp, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                File.Delete(tmp); // in use: keep the existing copy
            }
        }
        return dir;
    }
}
