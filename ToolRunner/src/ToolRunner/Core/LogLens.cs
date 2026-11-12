using System.Text.RegularExpressions;

namespace ToolRunner.Core;

/// A group of log lines that are the same message apart from numbers, ids and timestamps.
public sealed record LogPattern(string Level, string Template, int Count, string Sample, int FirstIndex);

/// Makes chatty logs readable: classifies each line by severity and collapses repeats into patterns with counts,
/// so "the one error among 5,000 heartbeats" is the first thing you see.
public static class LogLens
{
    private static readonly Regex Error = new(@"\b(fail|crit|critical|error|fatal)\b|\[(ERR|FTL|ERROR|FATAL)\]|Exception\b|Unhandled", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Warn = new(@"\bwarn(ing)?\b|\[(WRN|WARN)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Debug = new(@"\b(dbug|debug|trce|trace|verbose)\b|\[(DBG|VRB)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Info = new(@"\binfo(rmation)?\b|\[INF\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StackFrame = new(@"^\s+(at |--- )", RegexOptions.Compiled);

    private static readonly Regex Timestamp = new(@"^\s*\[?\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?\]?\s*", RegexOptions.Compiled);
    private static readonly Regex Guid = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"\b(0x)?[0-9a-fA-F]{8,}\b", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\d+([.,:]\d+)*", RegexOptions.Compiled);

    public static readonly string[] Levels = { "error", "warn", "info", "debug", "other" };

    public static string Level(string line) =>
        StackFrame.IsMatch(line) || Error.IsMatch(line) ? "error" : Warn.IsMatch(line) ? "warn" : Debug.IsMatch(line) ? "debug" : Info.IsMatch(line) ? "info" : "other";

    /// The line with everything that varies between repeats replaced, so repeats compare equal.
    public static string Template(string line)
    {
        var s = Timestamp.Replace(line, "");
        s = Timestamp.Replace(s, "");                 // docker -t adds its own timestamp in front of the app's
        s = Guid.Replace(s, "<id>");
        s = Hex.Replace(s, "<hex>");
        s = Number.Replace(s, "#");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// Patterns ordered by severity, then by first appearance.
    public static List<LogPattern> Digest(IEnumerable<string> lines) =>
        lines.Select((l, i) => (Line: l.TrimEnd('\r'), Index: i)).Where(x => x.Line.Trim().Length > 0)
             .GroupBy(x => (Level: Level(x.Line), Template: Template(x.Line)))
             .Select(g => new LogPattern(g.Key.Level, g.Key.Template, g.Count(), g.First().Line, g.First().Index))
             .OrderBy(p => Array.IndexOf(Levels, p.Level)).ThenBy(p => p.FirstIndex).ToList();

    public static IEnumerable<string> Text(IReadOnlyList<LogPattern> patterns, int max = 60)
    {
        var counts = string.Join(", ", Levels.Select(l => (l, n: patterns.Where(p => p.Level == l).Sum(p => p.Count))).Where(x => x.n > 0).Select(x => $"{x.n} {x.l}"));
        yield return $"digest: {patterns.Sum(p => p.Count)} line(s) in {patterns.Count} pattern(s): {counts}";
        foreach (var p in patterns.Take(max)) yield return $"[{p.Level,-5}] x{p.Count,-5} {(p.Count == 1 ? p.Sample.Trim() : p.Template)}";
        if (patterns.Count > max) yield return $"... {patterns.Count - max} more pattern(s) not shown";
    }
}
