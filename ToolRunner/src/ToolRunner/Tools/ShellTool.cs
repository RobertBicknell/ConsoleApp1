using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tools;

/// Any shell command as a step. ${NAME} in the command is replaced with toolrunner's variables before it runs, and
/// what it prints can be captured back into variables. Verbs: run, query.
///   run    a command that may change things (blocked while the read-only switch is on).
///   query  the same, for a command YOU know only reads. The tool cannot check that; the verb is your statement.
/// Shells: cmd (default on this machine), powershell, sh (default on an ssh host), bash.
/// ${NAME} always means a toolrunner variable; write the shell's own as %NAME%, $env:NAME or $NAME.
public sealed class ShellTool : ITool
{
    public string Name => "shell";

    private static readonly Arg[] Args_ = A.Args(
        A.Str("command", "The command line (or several lines). ${NAME} is replaced with the variable's value first.", true),
        A.Enum("shell", "Which shell runs it. Default: cmd locally on Windows, sh otherwise.", "cmd", "powershell", "sh", "bash"),
        A.Str("workDir", "Directory to run in."),
        A.Str("into", "Variable that receives everything printed to stdout, trimmed (default SHELL_OUT)."),
        A.Enum("capture", "Turn the output into variables. lines: every NAME=value line. json: stdout is a JSON object; each property becomes a variable. Default none.", "none", "lines", "json"),
        A.Str("regex", "A regular expression applied to stdout; each named group (?<NAME>...) of the first match becomes a variable."),
        A.Str("prefix", "Put this in front of every captured variable name."),
        A.Int("expectExit", "The exit code that means success (default 0)."),
        A.Bool("show", "Echo the output to the log (default true)."));

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Change("run", "Run a shell command; capture its output into variables.", Args_),
        A.Verb("query", "Run a shell command that only reads (your responsibility); capture its output into variables.", Args_)
    };

    /// NAME=value lines of a text.
    public static Dictionary<string, string> Lines(string text) =>
        text.Split('\n').Select(l => Regex.Match(l.TrimEnd('\r'), @"^\s*([A-Za-z_][A-Za-z0-9_.]*)=(.*)$")).Where(m => m.Success)
            .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value.Trim());

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        if (c.Verb is not ("run" or "query")) return ToolResult.Fail($"Unknown verb '{c.Verb}'");
        var command = c.Str("command");
        var local = c.Host is LocalHost;
        var shell = c.Opt("shell") ?? (local && OperatingSystem.IsWindows() ? "cmd" : "sh");
        var show = c.Bool("show", true);

        ProcResult r;
        string? temp = null;
        try
        {
            switch (shell)
            {
                case "cmd":
                    // a batch file, not `cmd /c "..."`: quotes, pipes and several lines then mean what they say
                    if (!local) return ToolResult.Fail("shell cmd only exists on this machine; use sh or bash on an ssh host");
                    temp = Path.Combine(Path.GetTempPath(), $"toolrunner-{Guid.NewGuid():N}.cmd");
                    await File.WriteAllTextAsync(temp, "@echo off\r\n" + command.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", c.Ct);
                    r = await c.Exec("cmd", ["/d", "/c", temp], c.Opt("workDir"), show);
                    break;
                case "powershell":
                    r = await c.Exec("powershell", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], c.Opt("workDir"), show);
                    break;
                case "sh": case "bash":
                    r = await c.Exec(shell, ["-c", command], c.Opt("workDir"), show);
                    break;
                default: return ToolResult.Fail($"unknown shell '{shell}' (cmd, powershell, sh, bash)");
            }
        }
        catch (System.ComponentModel.Win32Exception e) { return ToolResult.Fail($"could not start {shell}: {e.Message}"); }
        finally { if (temp is not null) File.Delete(temp); }

        var stdout = r.Stdout.Replace("\r\n", "\n").TrimEnd();
        var prefix = c.Opt("prefix") ?? "";
        var outputs = new Dictionary<string, string> { [c.Str("into", "SHELL_OUT")] = stdout, ["SHELL_EXIT"] = r.Code.ToString() };

        var captured = new Dictionary<string, string>();
        switch (c.Opt("capture") ?? "none")
        {
            case "lines": captured = Lines(stdout); break;
            case "json":
                try
                {
                    if (JsonNode.Parse(stdout) is not JsonObject o) return ToolResult.Fail("capture=json: the output is not a JSON object");
                    foreach (var (k, v) in o) captured[k] = ToolContext.NodeStr(v) ?? "";
                }
                catch (JsonException e) { return ToolResult.Fail($"capture=json: the output is not JSON ({e.Message})"); }
                break;
        }
        if (c.Opt("regex") is { } pattern)
        {
            var rx = new Regex(pattern, RegexOptions.Multiline);
            var m = rx.Match(stdout);
            if (!m.Success) return ToolResult.Fail($"the output does not match the regex {pattern}");
            foreach (var name in rx.GetGroupNames().Where(n => !int.TryParse(n, out _))) captured[name] = m.Groups[name].Value;
        }
        foreach (var (k, v) in captured) { outputs[prefix + k] = v; c.Log($"captured {prefix + k} = {v}"); }

        var expect = c.Int("expectExit", 0);
        return r.Code == expect
            ? ToolResult.Success($"{shell} exited {r.Code}; {captured.Count} variable(s) captured", outputs)
            : ToolResult.Fail($"{shell} exited {r.Code} (expected {expect}){(r.Stderr.Trim() is { Length: > 0 } err ? ": " + err.Split('\n')[0].Trim() : "")}");
    }
}
