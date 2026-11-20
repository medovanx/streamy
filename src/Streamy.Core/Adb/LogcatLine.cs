using System.Globalization;
using System.Text.RegularExpressions;

namespace Streamy.Core.Adb;

/// <summary>One line of "adb logcat -v threadtime": "11-20 06:55:59.781  1234  1250 E Tag: message".</summary>
public sealed partial record LogcatLine(string Time, int Pid, int Tid, char Level, string Tag, string Message)
{
    /// <summary>Verbose, Debug, Info, Warning, Error, Fatal: higher is more serious.</summary>
    public int Severity => LevelOf(Level);

    public static int LevelOf(char level) => level switch
    {
        'V' => 0,
        'D' => 1,
        'I' => 2,
        'W' => 3,
        'E' => 4,
        'F' or 'A' => 5,
        _ => 0,
    };

    /// <summary>The logcat command: the last <paramref name="history"/> lines, then new ones as they come.</summary>
    public static IReadOnlyList<string> Command(int history) => ["logcat", "-v", "threadtime", "-T", history.ToString(CultureInfo.InvariantCulture)];

    [GeneratedRegex(@"^(\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\s+(\d+)\s+(\d+)\s+([VDIWEFA])\s+(.*?)\s*: (.*)$")]
    private static partial Regex Pattern();

    /// <summary>Null for lines that are not log entries ("--------- beginning of main").</summary>
    public static LogcatLine? Parse(string line)
    {
        var m = Pattern().Match(line);
        if (!m.Success)
        {
            // "Tag:" with nothing after it.
            if (line.EndsWith(':') && Pattern().Match(line + " ") is { Success: true } m2) m = m2;
            else return null;
        }
        return new LogcatLine(
            m.Groups[1].Value,
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            m.Groups[4].Value[0],
            m.Groups[5].Value,
            m.Groups[6].Value);
    }

    /// <summary>True when the filter text appears in the tag or message (any case), or in the process id.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Tag.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Message.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Pid.ToString(CultureInfo.InvariantCulture) == filter;

    public override string ToString() => $"{Time} {Pid,5} {Tid,5} {Level} {Tag}: {Message}";
}
