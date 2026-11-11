using System.Globalization;
using System.Text.RegularExpressions;

namespace DSAceLogViewer.Core;

public enum LogCategory { Chat, OOC, Login, Traffic, Combat, System, Other }

public sealed record LogEntry(long Number, string Timestamp, string Message, string Raw, LogCategory Category)
{
    public string CategoryName => Category.ToString();
    public string Color => GetCategoryColor(Category);

    public static string GetCategoryColor(LogCategory category) => category switch
    {
        LogCategory.OOC => "#C5A5FF", LogCategory.Login => "#BCED75",
        LogCategory.Traffic => "#68D4E8", LogCategory.Combat => "#FF9B91",
        LogCategory.Chat => "#D4E3F5", LogCategory.System => "#E7C786", _ => "#93A5BE"
    };
}

public static partial class LogParser
{
    [GeneratedRegex(@"^\[(?<time>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})\]\s*")]
    private static partial Regex TimestampRegex();
    [GeneratedRegex(@"(?<!^)(?=\[\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}\])")]
    private static partial Regex JoinedEntriesRegex();
    [GeneratedRegex(@"^(?:(?:\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s+\S+\s+)?Traffic control\b|\[Traffic\])", RegexOptions.IgnoreCase)]
    private static partial Regex TrafficRegex();
    [GeneratedRegex(@"^(?:\[(?:OOC|Out.of.character)\]|\(OOC\))", RegexOptions.IgnoreCase)]
    private static partial Regex OocRegex();
    [GeneratedRegex(@"^(?:(?:Login|Logout|Logon|Logoff|Connect|Disconnect)(?:\s*[:\[]|\s+event\b)|\[(?:Login|Logout|Connect|Disconnect)\]|(?:Player\s+)?\S+ (?:has |just )?(?:logged (?:in|out)|connected|disconnected|entered the game|left the game)\b|Welcome to the Discovery Freelancer|Initialis?ing\b|Freelancer Started\b|Shutdown\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRegex();
    [GeneratedRegex(@"^(?:Death:|\[(?:Death|Combat|Kill)\])", RegexOptions.IgnoreCase)]
    private static partial Regex CombatRegex();
    [GeneratedRegex(@"^(?:(?:Rules Tip|Tip|ERR|ERROR|WARNING|INFO|Auto-Buy\([^)]*\))\s*:|OK\s|Options:|\s*-(?:log|localtime|showinfocards)|Screen size|Invalid display|Rearmament available|Jump systems|Cloaking device|Docking at|A bounty of|Welcome administrator|Permitted player-owned|To (?:type|see|learn)|Discovery is a|Players are required|Please (?:visit|check)|The year is|The solar winds|Unsure about)", RegexOptions.IgnoreCase)]
    private static partial Regex SystemRegex();
    [GeneratedRegex(@"^(?:\[[^\]\r\n]+\]\s*)?[^:\r\n]{1,80}:\s")]
    private static partial Regex ChatRegex();

    // DSAce sometimes writes Shutdown directly before the next timestamp.
    public static IEnumerable<string> SplitJoinedEntries(string line) => JoinedEntriesRegex().Split(line).Where(s => s.Length > 0);

    public static LogEntry Parse(string raw, long number)
    {
        var match = TimestampRegex().Match(raw);
        var timestamp = match.Success ? match.Groups["time"].Value : "";
        var message = match.Success ? raw[match.Length..] : raw;
        var category = OocRegex().IsMatch(message) ? LogCategory.OOC
            : TrafficRegex().IsMatch(message) ? LogCategory.Traffic
            : CombatRegex().IsMatch(message) ? LogCategory.Combat
            : LoginRegex().IsMatch(message) ? LogCategory.Login
            : SystemRegex().IsMatch(message) ? LogCategory.System
            : ChatRegex().IsMatch(message) ? LogCategory.Chat : LogCategory.Other;
        return new(number, timestamp, message, raw, category);
    }
}
