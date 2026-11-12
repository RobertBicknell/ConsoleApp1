using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// One place a variable appears. Kind: interpolation | interpolation-default (${X:-d} in compose),
/// environment (a service's `environment:` key), env-file (a key in the project env file, used for ${} interpolation),
/// container-env-file (a key in a service's `env_file:`, delivered into the container).
public sealed record VarRef(string Name, string Kind, string? Service, string? Value, string File, int Line);

/// One variable across everything scanned. Status: ok | default | undefined | compose-literal | container-env | unused.
/// Value is the effective value, as it is; the spool file hides secret-looking ones when it prints them.
public sealed record ScanRow(string Name, string Category, string Status, string? Value, IReadOnlyList<VarRef> Refs);

/// Finds every environment variable the stack mentions, from the compose file and env files, line-based on purpose
/// (a YAML round-trip loses comments, and `docker compose config` needs docker, which a dev box may not have).
public static class Scanner
{
    private static readonly Regex Braced = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-?+])([^}]*))?\}", RegexOptions.Compiled);
    private static readonly Regex Bare = new(@"(?<!\$)\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex Key = new(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$", RegexOptions.Compiled);

    /// Text before the first '.', else before the first '_', so services.*, ssh.*, COMPOSE_*, DB_* group together.
    public static string Category(string k) =>
        k.IndexOf('.') is > 0 and var d ? k[..d] : k.IndexOf('_') is > 0 and var u ? k[..u] : "other";

    public static (List<VarRef> Refs, List<(string Path, string? Service)> EnvFiles) Compose(string file, string text)
    {
        var refs = new List<VarRef>();
        var envFiles = new List<(string, string?)>();
        string? service = null, block = null;
        var inServices = false;
        int? svcIndent = null;
        var blockIndent = 0;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var indent = l.Length - l.TrimStart().Length;

            foreach (Match m in Braced.Matches(l))
                refs.Add(new VarRef(m.Groups[1].Value, m.Groups[2].Value.Contains('-') ? "interpolation-default" : "interpolation",
                                    service, m.Groups[3].Success ? m.Groups[3].Value : null, file, i + 1));
            foreach (Match m in Bare.Matches(Braced.Replace(l, "")))
                refs.Add(new VarRef(m.Groups[1].Value, "interpolation", service, null, file, i + 1));

            if (indent == 0) { inServices = t == "services:"; service = block = null; svcIndent = null; continue; }
            if (!inServices) continue;

            svcIndent ??= indent;
            if (indent == svcIndent && Regex.Match(t, @"^([\w.\-]+):\s*(#.*)?$") is { Success: true } sm) { service = sm.Groups[1].Value; block = null; continue; }
            if (service is null) continue;

            if (block is not null && indent <= blockIndent) block = null;
            if (block is null)
            {
                if (t.StartsWith("environment:")) { block = "environment"; blockIndent = indent; }
                else if (t.StartsWith("env_file:"))
                {
                    block = "env_file"; blockIndent = indent;
                    var inline = t["env_file:".Length..].Trim();
                    if (inline.Length > 0 && !inline.StartsWith('[')) envFiles.Add((Unquote(inline), service));
                }
                continue;
            }

            var item = t.StartsWith("- ") ? t[2..].Trim() : t;
            if (block == "env_file") { if (t.StartsWith("- ")) envFiles.Add((Unquote(item), service)); continue; }

            // environment: "- KEY=VAL", "- KEY", or "KEY: VAL"
            var em = Regex.Match(item, @"^([A-Za-z_][A-Za-z0-9_]*)\s*(?:[=:]\s*(.*))?$");
            if (em.Success) refs.Add(new VarRef(em.Groups[1].Value, "environment", service, em.Groups[2].Success ? Unquote(em.Groups[2].Value) : null, file, i + 1));
        }
        return (refs, envFiles);
    }

    public static List<VarRef> EnvFile(string file, string text, string kind, string? service = null)
    {
        var refs = new List<VarRef>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (!lines[i].TrimStart().StartsWith('#') && Key.Match(lines[i].TrimEnd('\r')) is { Success: true } m)
                refs.Add(new VarRef(m.Groups[1].Value, kind, service, Unquote(m.Groups[2].Value.Trim()), file, i + 1));
        return refs;
    }

    public static List<ScanRow> Merge(IEnumerable<VarRef> refs) =>
        refs.GroupBy(r => r.Name).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var kinds = g.Select(r => r.Kind).ToHashSet();
            var used = kinds.Contains("interpolation") || kinds.Contains("interpolation-default");
            var env = g.FirstOrDefault(r => r.Kind == "env-file");
            var def = g.FirstOrDefault(r => r.Kind == "interpolation-default");
            // An environment value that just forwards ${NAME} is a reference, not a value of its own.
            var lit = g.FirstOrDefault(r => r.Kind is "environment" or "container-env-file" && r.Value is not null
                                            && !r.Value.Contains("${" + g.Key, StringComparison.Ordinal));
            var status = used ? (env is not null ? "ok" : def is not null ? "default" : "undefined")
                       : kinds.Contains("environment") ? "compose-literal"
                       : kinds.Contains("container-env-file") ? "container-env" : "unused";
            var value = env?.Value ?? def?.Value ?? lit?.Value;
            return new ScanRow(g.Key, Category(g.Key), status, value, g.ToList());
        }).ToList();

    /// Reads COMPOSE_FILE and ENV_FILE (and every service env_file:) on the host and merges the findings.
    /// Returns the rows plus notes about files that were named but could not be read.
    public static async Task<(List<ScanRow> Rows, List<string> Notes)> RunAsync(RunState state, IHost host, CancellationToken ct)
    {
        var notes = new List<string>();
        var refs = new List<VarRef>();
        string? V(string name) => state.Vars.TryGetValue(name, out var v) ? state.Expand(v) : null;

        if (V("COMPOSE_FILE") is { } compose)
        {
            var text = await host.ReadTextAsync(compose, ct);
            if (text is null) notes.Add($"compose file not found: {compose}");
            else
            {
                var (r, envFiles) = Compose(compose, text);
                refs.AddRange(r);
                var dir = compose[..Math.Max(compose.LastIndexOfAny(['/', '\\']), 0)];
                foreach (var (path, svc) in envFiles.Distinct())
                {
                    var full = Path.IsPathRooted(path) || path.StartsWith('/') ? path : host.Combine(dir, path);
                    if (compose.Contains('/')) full = full.Replace('\\', '/');    // keep one separator style in the output
                    var et = await host.ReadTextAsync(full, ct);
                    if (et is null) notes.Add($"env_file of '{svc}' not found: {full}");
                    else refs.AddRange(EnvFile(full, et, "container-env-file", svc));
                }
            }
        }
        else notes.Add("COMPOSE_FILE is not defined in globals");

        if (V("ENV_FILE") is { } envFile)
        {
            var text = await host.ReadTextAsync(envFile, ct);
            if (text is null) notes.Add($"env file not found: {envFile}");
            else refs.AddRange(EnvFile(envFile, text, "env-file"));
        }
        return (Merge(refs), notes);
    }

    private static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s[1..^1] : s;
}
