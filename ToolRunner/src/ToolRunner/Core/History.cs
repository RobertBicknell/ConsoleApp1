using System.Text.Json;

namespace ToolRunner.Core;

/// One finished workflow / `run` invocation: what was asked, the human log it produced, and the values tools wrote.
public sealed record RunRecord(DateTimeOffset At, string Host, string[] Argv, int Exit, string Log, Dictionary<string, string> Produced);

/// Append-only journal, one JSON object per line, in <config dir>/../history/history.jsonl (gitignored).
/// Local to the machine running toolrunner, even with --host (the server's paths are not ours).
public static class History
{
    private static readonly JsonSerializerOptions Line = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Dir(string configDir) => Path.GetFullPath(Path.Combine(configDir, "..", "history"));
    private static string File_(string configDir) => Path.Combine(Dir(configDir), "history.jsonl");

    public static void Append(string configDir, RunRecord r)
    {
        Directory.CreateDirectory(Dir(configDir));
        File.AppendAllText(File_(configDir), JsonSerializer.Serialize(r, Line) + "\n");
    }

    /// A note written into the stream by the user: a finding, a milestone, a summary to pick up later.
    public static void Remark(string configDir, string text) =>
        Append(configDir, new RunRecord(DateTimeOffset.UtcNow, "-", ["remark"], 0, text.Trim(), new()));

    public static bool IsRemark(RunRecord r) => r.Argv is ["remark"];

    /// The last n records, oldest first. Unreadable lines are skipped (a half-written line must not break spooling).
    public static IReadOnlyList<RunRecord> Last(string configDir, int n)
    {
        var path = File_(configDir);
        if (!File.Exists(path)) return [];
        return File.ReadLines(path).Select(Parse).OfType<RunRecord>().TakeLast(n).ToList();
    }

    private static RunRecord? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<RunRecord>(line, Line); }
        catch (JsonException) { return null; }
    }
}
