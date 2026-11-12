using System.Globalization;
using System.Text.RegularExpressions;
using ToolRunner.Core;

namespace ToolRunner.Tools;

/// Search a log FILE on the host. Verb: search.
/// Lines are windowed by the timestamp at the start of each line (ISO-8601; no offset means UTC).
/// For container logs prefer docker/logs, which lets docker apply --since/--until itself.
public sealed class LogFileTool : ITool
{
    public string Name => "logfile";
    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("search", "Search a log file on the host, windowed by the leading timestamp of each line.", A.Args(
            A.Str("file", "Log file path on the host.", true), A.Str("since", "Default: STARTED_AT_UTC."), A.Str("until", "Upper bound."),
            A.Int("waitSeconds", "Poll up to this long for the expectation to hold.")), LogMatcher.Spec)
    };

    private static readonly Regex Ts = new(
        @"^\s*\[?(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)", RegexOptions.Compiled);

    private static DateTimeOffset? Parse(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        if (c.Verb != "search") return ToolResult.Fail($"Unknown verb '{c.Verb}'");
        var file = c.Str("file");
        var since = Parse(c.Opt("since") ?? c.Var("STARTED_AT_UTC"));
        var until = Parse(c.Opt("until"));
        var m = new LogMatcher(c);

        var (text, lines, matches, ok) = await m.PollAsync(async () =>
        {
            var content = await c.Host.ReadTextAsync(file, c.Ct) ?? "";
            if (since is null && until is null) return content;
            return string.Join('\n', content.Split('\n').Where(l =>
            {
                var t = Ts.Match(l);
                var at = t.Success ? Parse(t.Groups[1].Value) : null;
                return at is not null && (since is null || at >= since) && (until is null || at <= until);
            }));
        }, c.Int("waitSeconds", 0), c.Ct);

        m.Show(c, text, matches);
        var outs = LogMatcher.Outputs(c, "LOGFILE", text, lines, matches);
        var window = $"{(since?.ToString("O") ?? "start")} .. {(until?.ToString("O") ?? "end")}";
        var summary = $"{lines} line(s) in window {window}, {matches.Count} match(es)";
        return ok ? ToolResult.Success(summary, outs)
                  : ToolResult.Fail(m.Absent ? $"unwanted text present: {summary}" : $"expected text not found: {summary}");
    }
}
