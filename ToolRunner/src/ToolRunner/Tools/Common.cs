using System.Text.RegularExpressions;
using ToolRunner.Core;

namespace ToolRunner.Tools;

/// Shared "does this text contain X" logic for docker logs, docker events and log files.
/// Args: contains | regex, expect ("present" default | "absent").
public static class Time
{
    /// RFC 3339 in UTC with milliseconds, e.g. 2026-11-08T16:36:30.294Z: unambiguous for `docker --since/--until`.
    public static string Rfc3339(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class LogMatcher
{
    private readonly string? _contains;
    private readonly Regex? _regex;
    public bool Absent { get; }
    /// The arguments every log-searching verb shares.
    public static Arg[] Spec { get; } = A.Args(
        A.Str("contains", "Text a line must contain to match."),
        A.Str("regex", "Regular expression a line must match."),
        A.Enum("expect", "present (default): fail if nothing matches; absent: fail if anything matches.", "present", "absent"),
        A.Str("into", "State variable that receives the full text."),
        A.Str("save", "File (on this machine) to write the full text to."),
        A.Bool("show", "Echo the text to the log when there is no pattern (default true)."),
        A.Bool("digest", "Log a digest instead of the raw text: repeats collapsed into patterns with counts, errors first."));

    public bool HasPattern =>_contains is not null || _regex is not null;

    public LogMatcher(ToolContext c)
    {
        _contains = c.Opt("contains");
        if (c.Opt("regex") is { } rx) _regex = new Regex(rx, RegexOptions.Compiled);
        Absent = string.Equals(c.Opt("expect"), "absent", StringComparison.OrdinalIgnoreCase);
    }

    public (int Lines, List<string> Matches) Evaluate(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        var matches = lines.Where(l => (_contains is not null && l.Contains(_contains, StringComparison.Ordinal))
                                    || (_regex is not null && _regex.IsMatch(l))).ToList();
        return (lines.Count, matches);
    }

    public bool Satisfied(int matchCount) => !HasPattern || (Absent ? matchCount == 0 : matchCount > 0);

    /// Polls `fetch` until the expectation holds or `waitSeconds` elapse. With expect=absent the wait is an
    /// observation window: it ends early only if the unwanted text appears.
    public async Task<(string Text, int Lines, List<string> Matches, bool Ok)> PollAsync(
        Func<Task<string>> fetch, int waitSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            var text = await fetch();
            var (lines, matches) = Evaluate(text);
            var done = !HasPattern || matches.Count > 0 || DateTime.UtcNow >= deadline;
            if (done) return (text, lines, matches, Satisfied(matches.Count));
            await Task.Delay(1000, ct);
        }
    }

    /// Writes outputs, optional `save` file (on the machine running toolrunner) and `into` (full text in state).
    public static Dictionary<string, string> Outputs(ToolContext c, string prefix, string text, int lines, List<string> matches)
    {
        var o = new Dictionary<string, string>
        {
            [$"{prefix}_LINES"] = lines.ToString(),
            [$"{prefix}_MATCHES"] = matches.Count.ToString()
        };
        if (matches.Count > 0) o[$"{prefix}_FIRST_MATCH"] = matches[0];
        if (c.Opt("into") is { } into) o[into] = text;
        if (c.Opt("save") is { } save)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(save))!);
            File.WriteAllText(save, text);
        }
        return o;
    }

    public void Show(ToolContext c, string text, List<string> matches)
    {
        if (c.Bool("digest", false)) { foreach (var l in LogLens.Text(LogLens.Digest(text.Split('\n')))) c.Log(l); return; }
        if (HasPattern) foreach (var m in matches.Take(3)) c.Log(m);
        else if (c.Bool("show", true)) foreach (var l in text.Split('\n').Take(200)) c.Log(l.TrimEnd('\r'));
    }
}
