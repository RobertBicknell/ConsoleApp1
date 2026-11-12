using System.Text.RegularExpressions;
using ToolRunner.Core;

namespace ToolRunner.Tools;

/// File operations on the selected host. Verbs: derive, delete.
///
/// derive NEVER edits the source. It writes a modified copy (the "override") and leaves the original
/// untouched, so undo is simply "stop using / delete the copy", and `git diff --no-index` shows exactly
/// what differs. Each edit must match exactly the number of lines it expects (default 1), otherwise the
/// whole derive fails and nothing is written.
///
/// Edits:
///   { "match": "text", "replace": "text", "expect": 1 }      swap text inside the matching line(s)
///   { "set": "KEY", "value": "v", "addIfMissing": false }    replace the KEY=... line of an env file
///   { "commentService": "name" }                              comment out a service block under services:
///   { "dropDependsOn": "name" }                               comment out other services' depends_on entries for it
/// Commented-out lines never match (set "ignoreComments": false on an edit to change that).
public sealed class FileTool : ITool
{
    public string Name => "file";
    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Change("derive", "Write a modified copy of a file; the source is never touched.", A.Args(
            A.Str("source", "File to copy from.", true), A.Str("target", "File to write.", true),
            new Arg("edits", "edits", "Edits; each must match exactly its expected number of lines.", true))),
        A.Change("delete", "Delete files (typically generated copies).", A.Args(A.List("paths", "Files to delete."), A.Str("path", "One file to delete.")))
    };

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        switch (c.Verb)
        {
            case "delete":
            {
                var paths = c.List("paths"); if (c.Opt("path") is { } one) paths.Add(one);
                var removed = 0;
                foreach (var p in paths) if (await c.Host.DeleteAsync(p, c.Ct)) removed++;
                return ToolResult.Success($"removed {removed} of {paths.Count} file(s)");
            }
            case "derive": return await Derive(c);
            default: return ToolResult.Fail($"Unknown verb '{c.Verb}'");
        }
    }

    private static bool IsComment(string l) => l.TrimStart().StartsWith('#');

    private async Task<ToolResult> Derive(ToolContext c)
    {
        var source = c.Str("source");
        var target = c.Str("target");
        if (source == target)
            return ToolResult.Fail("source and target are the same file; derive never edits in place");

        var text = await c.Host.ReadTextAsync(source, c.Ct);
        if (text is null) return ToolResult.Fail($"source not found: {source}");

        // Real files mix line endings. Split on \n, drop any \r, and rejoin with the dominant style.
        var crlf = text.Split("\r\n").Length - 1;
        var nl = crlf * 2 >= text.Split('\n').Length - 1 && crlf > 0 ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();      // a trailing newline yields a final "" element, preserved on join

        if (c.Args["edits"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 })
            return ToolResult.Fail("'edits' must be a non-empty JSON array (a derive with no edits would silently do nothing)");

        var n = 0;
        foreach (var e in c.Objects("edits"))
        {
            n++;
            var ignoreComments = !string.Equals(ToolContext.NodeStr(e["ignoreComments"]), "false", StringComparison.OrdinalIgnoreCase);
            var expect = int.Parse(ToolContext.NodeStr(e["expect"]) ?? "1");

            if (ToolContext.NodeStr(e["commentService"]) is { } service)
            {
                var err = CommentService(lines, service);
                if (err is not null) return ToolResult.Fail($"edit {n} (commentService {service}): {err}");
            }
            else if (ToolContext.NodeStr(e["dropDependsOn"]) is { } dep)
            {
                // Zero references is fine (nothing depended on it) unless the edit states how many it expects.
                var dropped = DropDependsOn(lines, dep);
                if (e["expect"] is not null && dropped != expect)
                    return ToolResult.Fail($"edit {n} (dropDependsOn {dep}): expected {expect} reference(s), found {dropped}");
                c.Log($"relaxed {dropped} depends_on reference(s) to '{dep}'");
            }
            else if (ToolContext.NodeStr(e["set"]) is { } key)
            {
                var value = ToolContext.NodeStr(e["value"]) ?? throw new ArgumentException($"edit {n}: 'set' needs 'value'");
                var rx = new Regex($@"^(\s*export\s+)?{Regex.Escape(key)}\s*=");
                var hits = Enumerable.Range(0, lines.Count).Where(i => rx.IsMatch(lines[i]) && !(ignoreComments && IsComment(lines[i]))).ToList();
                var addIfMissing = string.Equals(ToolContext.NodeStr(e["addIfMissing"]), "true", StringComparison.OrdinalIgnoreCase);
                if (hits.Count == 0 && addIfMissing)
                    lines.Insert(lines.Count > 0 && lines[^1] == "" ? lines.Count - 1 : lines.Count, $"{key}={value}");
                else if (hits.Count != 1)
                    return ToolResult.Fail($"edit {n} (set {key}): expected exactly 1 line, found {hits.Count}{Where(hits)}");
                else lines[hits[0]] = $"{rx.Match(lines[hits[0]]).Groups[1].Value}{key}={value}";
            }
            else if (ToolContext.NodeStr(e["match"]) is { } match)
            {
                var replace = ToolContext.NodeStr(e["replace"]) ?? throw new ArgumentException($"edit {n}: 'match' needs 'replace'");
                var hits = Enumerable.Range(0, lines.Count)
                    .Where(i => lines[i].Contains(match, StringComparison.Ordinal) && !(ignoreComments && IsComment(lines[i]))).ToList();
                if (hits.Count != expect)
                    return ToolResult.Fail($"edit {n} (match '{match}'): expected {expect} line(s), found {hits.Count}{Where(hits)}");
                foreach (var i in hits) lines[i] = lines[i].Replace(match, replace, StringComparison.Ordinal);
            }
            else return ToolResult.Fail($"edit {n}: needs one of 'match', 'set', 'commentService'");
        }

        lines.Insert(0, $"# GENERATED by toolrunner from {source} - do not edit; delete to revert");
        await c.Host.WriteTextAsync(target, string.Join(nl, lines), c.Ct);
        return ToolResult.Success($"wrote {target} ({n} edit(s)); {source} untouched");
    }

    /// Comments out every live depends_on entry that names `service` (list, flow and map forms, with a map entry's
    /// nested condition lines), and the depends_on key itself when nothing is left under it. Returns how many.
    private static int DropDependsOn(List<string> lines, string service)
    {
        var count = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsComment(lines[i]) || Regex.Match(lines[i], @"^(\s+)depends_on:\s*(.*)$") is not { Success: true } m) continue;
            var ind = m.Groups[1].Length;
            var inline = m.Groups[2].Value.Trim();
            if (inline.StartsWith('['))
            {
                var items = inline.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                var kept = items.Where(x => x.Trim('"', '\'') != service).ToList();
                if (kept.Count == items.Count) continue;
                count += items.Count - kept.Count;
                lines[i] = kept.Count == 0 ? "#" + lines[i] : $"{m.Groups[1].Value}depends_on: [{string.Join(", ", kept)}]";
                continue;
            }

            int? childIndent = null;
            var live = 0;
            var inTarget = false;
            var toComment = new List<int>();
            for (var j = i + 1; j < lines.Count; j++)
            {
                var t = lines[j];
                if (t.Trim().Length == 0 || IsComment(t)) continue;
                var ci = t.Length - t.TrimStart().Length;
                if (ci <= ind) break;
                childIndent ??= ci;
                if (ci == childIndent)
                {
                    var e = Regex.Match(t.Trim(), @"^(?:-\s*[""']?(?<a>[\w.\-]+)[""']?\s*(#.*)?|(?<b>[\w.\-]+):.*)$");
                    inTarget = e.Success && (e.Groups["a"].Success ? e.Groups["a"].Value : e.Groups["b"].Value) == service;
                    if (inTarget) { toComment.Add(j); count++; } else live++;
                }
                else if (inTarget) toComment.Add(j);
            }
            foreach (var k in toComment) lines[k] = "#" + lines[k];
            if (toComment.Count > 0 && live == 0) lines[i] = "#" + lines[i];
        }
        return count;
    }

    private static string Where(List<int> hits) => hits.Count == 0 ? "" : $" (lines {string.Join(", ", hits.Select(i => i + 1))})";

    /// Line-based on purpose: a YAML round-trip through a parser typically loses comments and formatting.
    /// Assumes conventional space-indented block YAML.
    private static string? CommentService(List<string> lines, string service)
    {
        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^services:\s*$"));
        if (start < 0) return "no top-level 'services:' key";
        var end = lines.FindIndex(start + 1, l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && l[0] != '#');
        if (end < 0) end = lines.Count;

        var live = new Regex($@"^(\s+){Regex.Escape(service)}:\s*(#.*)?$");
        var hits = Enumerable.Range(start + 1, end - start - 1).Where(i => live.IsMatch(lines[i])).ToList();
        if (hits.Count != 1) return $"expected exactly 1 service block, found {hits.Count}";

        var i0 = hits[0];
        var baseIndent = live.Match(lines[i0]).Groups[1].Length;
        var last = i0;
        for (var j = i0 + 1; j < end; j++)
        {
            var t = lines[j].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (lines[j].Length - lines[j].TrimStart().Length <= baseIndent) break;
            last = j;
        }
        for (var j = i0; j <= last; j++)
            if (lines[j].Trim().Length > 0 && !lines[j].StartsWith('#')) lines[j] = "#" + lines[j];
        return null;
    }
}
