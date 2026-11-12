using System.Text;
using System.Text.RegularExpressions;

namespace ToolRunner.Core;

/// What to put in the spool file. Files are read from the machine running toolrunner.
/// Mode "ask": a question, answered in prose. Mode "plan": an intent, answered with steps chosen from OPTIONS
/// (templates spool.plan.header.txt / spool.plan.footer.txt; the caller adds the OPTIONS section).
public sealed record SpoolOptions(int Last, IReadOnlyList<string> Files, bool WithConfig, bool Defang, string? Note, string Mode = "ask");

/// Builds ONE plain-text file for pasting into a chat tool that copes badly with code and XML:
/// prose header, then sections of plain text, then a footer that dictates the shape of the answer.
/// Sections are delimited by lines starting with "#####" so they survive any paste.
public static class Spool
{
    public static bool IsSecret(string key) => Secret.IsMatch(key);
    private static readonly Regex Secret = new(@"password|passwd|secret|token|apikey|api_key|private|connectionstring|credential", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string DefaultHeader = "Context from the toolrunner tool follows, as plain text. Sections start with a line beginning #####.";
    private const string DefaultFooter = "Answer using the numbered sections requested here, then end with a section titled CARRY-FORWARD.";

    public static string Build(string configDir, RunState state, IReadOnlyList<RunRecord> runs, SpoolOptions o, IReadOnlyList<ScanRow>? scan = null, DirScan? dirScan = null, CompareResult? compare = null, IReadOnlyList<(string Title, string Body)>? extra = null)
    {
        var sb = new StringBuilder();
        void Section(string title, string body) => sb.Append("##### ").Append(title).Append('\n').Append(body.TrimEnd()).Append("\n\n");

        var plan = o.Mode == "plan";
        Section("INSTRUCTIONS", Read(Path.Combine(configDir, plan ? "spool.plan.header.txt" : "spool.header.txt")) ?? DefaultHeader);
        if (!string.IsNullOrWhiteSpace(o.Note)) Section(plan ? "MY INTENT" : "MY QUESTION", o.Note!);

        var carry = Path.Combine(History.Dir(configDir), "carry.txt");
        if (Read(carry) is { } prior) Section("PREVIOUS SUMMARY (your own CARRY-FORWARD from the last answer)", prior);

        Section($"RECENT RUNS (last {runs.Count}, oldest first)",
            runs.Count == 0 ? "(none recorded yet)" : string.Join("\n\n", runs.Select((r, i) => Run(i + 1, r))));

        Section("VARIABLES (resolved; values of names containing password/secret/token/apikey/private are hidden)",
            string.Join("\n", state.Vars.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase).Select(v => $"{v.Key} = {Show(state, v.Key, v.Value)}")));

        if (scan is not null)
            Section("ENVIRONMENT VARIABLES (scan of the compose and env files; secret-looking values hidden)",
                scan.Count == 0 ? "(none found)" : string.Join("\n", scan.Select(r =>
                    $"{r.Name} [{r.Status}] = {(r.Value is null ? "(no value)" : IsSecret(r.Name) ? "********" : r.Value)}   <- {string.Join("; ", r.Refs.Select(x => $"{x.Kind}{(x.Service is null ? "" : " " + x.Service)} {Path.GetFileName(x.File)}:{x.Line}"))}")));

        if (dirScan is not null)
            Section("STACK SCAN (compose stacks under a directory: ports, volumes, bind mounts; variable values are not shown)", StackScanner.Text(dirScan));

        if (compare is not null)
            Section("COMPARISON WITH REFERENCE (how this deployment differs from a known-good reference folder)", DeploymentCompare.Text(compare, hideSecrets: true));

        foreach (var (title, body) in extra ?? []) Section(title, body);

        var files = o.WithConfig
            ? Directory.EnumerateFiles(configDir, "*.json").Where(f => !f.EndsWith(".user.json")).Concat(o.Files)
            : o.Files;
        foreach (var f in files.Distinct())
            Section($"FILE {f}", File.Exists(f) ? File.ReadAllText(f) : "(file not found)");

        Section("REQUIRED ANSWER FORMAT", Read(Path.Combine(configDir, plan ? "spool.plan.footer.txt" : "spool.footer.txt")) ?? DefaultFooter);

        var text = sb.ToString();
        // Some chat tools choke on raw angle brackets; single-angle quotes look the same and are plain text.
        return o.Defang ? text.Replace('<', '‹').Replace('>', '›') : text;
    }

    private static string Run(int n, RunRecord r) => History.IsRemark(r) ? $"--- note {n} (written by me at {r.At:u}): {r.Log.TrimEnd()}" :
        $"--- run {n}: toolrunner {string.Join(' ', r.Argv)}\nat {r.At:u} on {r.Host}, exit {r.Exit}\n{r.Log.TrimEnd()}";

    private static string Show(RunState s, string key, string raw)
    {
        if (Secret.IsMatch(key)) return "********";
        try { return s.Expand(raw); } catch (InvalidOperationException e) { return $"<{e.Message}>"; }
    }

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}
