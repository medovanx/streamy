using System.Globalization;
using System.Text.RegularExpressions;

namespace Streamy.Core.Adb;

/// <summary>A notification on the phone, read from "dumpsys notification".</summary>
public sealed record PhoneNotification(string Key, string Package, int Flags, long When, string Title, string Text)
{
    private const int FlagOngoing = 0x2, FlagForegroundService = 0x40, FlagGroupSummary = 0x200;

    /// <summary>Worth showing on the PC: not a running service or media player, not a group's summary, and with words in it.</summary>
    public bool IsWorthShowing => (Flags & (FlagOngoing | FlagForegroundService | FlagGroupSummary)) == 0 && (Title.Length > 0 || Text.Length > 0);

    /// <summary>The same notification posted again (a new message in a chat) has a new time.</summary>
    public string Identity => $"{Key}@{When.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// The phone's notifications without an app on it: Android lets the shell read them through "dumpsys notification"
/// (it no longer lets it ask the notification service directly). A shell loop on the phone prints the few lines
/// that matter every few seconds, and <see cref="Parse"/> reads one round of them.
/// </summary>
public static partial class PhoneNotifications
{
    public const string EndMarker = "---STREAMY-END";

    /// <summary>A shell loop that prints the active notifications every <paramref name="seconds"/> seconds, each round ending in <see cref="EndMarker"/>.</summary>
    public static string WatchCommand(int seconds) =>
        "while true; do dumpsys notification --noredact | grep -E '^    NotificationRecord\\(|^      flags=|^            when=|android\\.(title|text|bigText)='; " +
        $"echo {EndMarker}; sleep {seconds.ToString(CultureInfo.InvariantCulture)}; done";

    [GeneratedRegex(@"NotificationRecord\(\S+: pkg=(\S+) .*? key=(\S+?):? Notification")]
    private static partial Regex RecordLine();

    [GeneratedRegex(@"^\s*android\.(title|text|bigText)=\w+ \((.*)\)\s*$")]
    private static partial Regex ExtraLine();

    public static IReadOnlyList<PhoneNotification> Parse(string round)
    {
        var list = new List<PhoneNotification>();
        string? key = null, pkg = null, title = null, text = null, big = null;
        int flags = 0;
        long when = 0;
        void Flush()
        {
            if (key is not null && pkg is not null)
                list.Add(new PhoneNotification(key, pkg, flags, when, title ?? "", big ?? text ?? ""));
            key = pkg = title = text = big = null;
            flags = 0;
            when = 0;
        }
        foreach (var raw in round.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (RecordLine().Match(line) is { Success: true } m)
            {
                Flush();
                pkg = m.Groups[1].Value;
                key = m.Groups[2].Value;
                continue;
            }
            if (key is null) continue;
            var t = line.Trim();
            if (t.StartsWith("flags=", StringComparison.Ordinal) && flags == 0)
            {
                int.TryParse(t["flags=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out flags);
            }
            else if (t.StartsWith("when=", StringComparison.Ordinal))
            {
                var v = t["when=".Length..].Split('/')[0];
                long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out when);
            }
            else if (ExtraLine().Match(line) is { Success: true } e)
            {
                switch (e.Groups[1].Value)
                {
                    case "title": title = e.Groups[2].Value.Trim(); break;
                    case "text": text = e.Groups[2].Value.Trim(); break;
                    case "bigText": big = e.Groups[2].Value.Trim(); break;
                }
            }
        }
        Flush();
        return list;
    }
}

/// <summary>
/// Tells which notifications are new. The first round only learns what is already there, so starting the watch
/// does not replay the whole shade.
/// </summary>
public sealed class NotificationTracker
{
    private HashSet<string>? _seen;

    public IReadOnlyList<PhoneNotification> Next(IReadOnlyList<PhoneNotification> round)
    {
        var now = round.Select(n => n.Identity).ToHashSet();
        if (_seen is null)
        {
            _seen = now;
            return [];
        }
        var fresh = round.Where(n => !_seen.Contains(n.Identity) && n.IsWorthShowing).ToList();
        _seen = now;
        return fresh;
    }
}
