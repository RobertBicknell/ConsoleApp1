using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// One difference between a deployment and its reference.
/// Area: file (present on one side, or differing only in line endings / comments), env, settings (JSON), compose,
/// text (any other text file), binary. Kind: added (only here), removed (only in the reference), changed.
/// Item names the thing inside the file (an env key, a config path, "gateway.image", "gateway port").
public sealed record Diff(string Area, string File, string Item, string Kind, string? Current, string? Reference);

public sealed record CompareResult(string Current, string Reference, int Same, IReadOnlyList<Diff> Diffs, IReadOnlyList<string> Notes)
{
    public int FilesOnlyHere => Diffs.Count(d => d is { Area: "file", Item: "", Kind: "added" });
    public int FilesOnlyInReference => Diffs.Count(d => d is { Area: "file", Item: "", Kind: "removed" });
    public int FilesDifferent => Diffs.Where(d => !(d.Area == "file" && d.Item == "")).Select(d => d.File).Distinct().Count();
}

/// Compares a deployment folder with a reference folder by MEANING where it can: env files by key, JSON settings by
/// config path (lists of plain values as sets), compose files by service, image, dependencies, ports, mounts and
/// environment (after ${} expansion from each side's own .env), binaries by SHA-256. Values are reported as they are. Formatting, comment and
/// line-ending differences are reported as such instead of as a wall of changed lines.
public static class DeploymentCompare
{
    private static readonly string[] TextExtensions =
        { ".yml", ".yaml", ".env", ".json", ".txt", ".config", ".xml", ".conf", ".ini", ".sh", ".ps1", ".md", ".cs", ".props", ".targets", ".toml", ".properties", ".sql", ".csproj", ".editorconfig" };

    private static string Name(string rel) => rel[(rel.LastIndexOf('/') + 1)..];
    private static bool IsEnv(string rel) => Name(rel) is var n && (n == ".env" || n.EndsWith(".env", StringComparison.OrdinalIgnoreCase) || n.StartsWith(".env.", StringComparison.OrdinalIgnoreCase));
    private static bool IsText(string rel) => IsEnv(rel) || !Name(rel).Contains('.') || TextExtensions.Any(e => rel.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    private static string Hide(Diff d, string? value, bool hide) => value is not null && hide && d.Area is "env" or "settings" or "compose" && value != "(listed)" && Spool.IsSecret(d.Item) ? "********" : value ?? "";

    /// ignoreLineEndings: count files that differ only in CRLF/LF as identical (right for a git reference, where
    /// checkout converts line endings and the difference says nothing about the deployment).
    public static async Task<CompareResult> RunAsync(IHost host, string current, string reference, int maxDepth, CancellationToken ct, bool ignoreLineEndings = false)
    {
        current = current.Replace('\\', '/').TrimEnd('/');
        reference = reference.Replace('\\', '/').TrimEnd('/');
        var notes = new List<string>();
        if (!await host.ExistsAsync(current, ct)) notes.Add($"folder not found: {current}");
        if (!await host.ExistsAsync(reference, ct)) notes.Add($"reference folder not found: {reference}");
        if (notes.Count > 0) return new CompareResult(current, reference, 0, [], notes);

        async Task<HashSet<string>> Files(string root) =>
            (await host.FindFilesAsync(root, ["*"], maxDepth, ct)).Select(f => f[(root.Length + 1)..]).ToHashSet(StringComparer.Ordinal);
        var here = await Files(current);
        var there = await Files(reference);

        var diffs = new List<Diff>();
        diffs.AddRange(here.Except(there).Order(StringComparer.Ordinal).Select(f => new Diff("file", f, "", "added", null, null)));
        diffs.AddRange(there.Except(here).Order(StringComparer.Ordinal).Select(f => new Diff("file", f, "", "removed", null, null)));

        var same = 0;
        async Task<Dictionary<string, string>> EnvOf(string root, string rel)      // the .env beside a compose file, for ${} expansion
        {
            var dir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] + "/" : "";
            var text = await host.ReadTextAsync($"{root}/{dir}.env", ct);
            return text is null ? new() : Scanner.EnvFile("", text, "env-file").ToDictionary(r => r.Name, r => r.Value ?? "", StringComparer.Ordinal);
        }

        foreach (var rel in here.Intersect(there).Order(StringComparer.Ordinal))
        {
            if (!IsText(rel))
            {
                var (ha, hb) = (await host.HashAsync($"{current}/{rel}", ct), await host.HashAsync($"{reference}/{rel}", ct));
                if (ha == hb) same++; else diffs.Add(new Diff("binary", rel, "content", "changed", ha?[..12], hb?[..12]));
                continue;
            }

            var a = await host.ReadTextAsync($"{current}/{rel}", ct) ?? "";
            var b = await host.ReadTextAsync($"{reference}/{rel}", ct) ?? "";
            if (a == b) { same++; continue; }
            if (a.Replace("\r\n", "\n") == b.Replace("\r\n", "\n"))
            {
                if (ignoreLineEndings) { same++; continue; }
                diffs.Add(new Diff("file", rel, "line endings only", "changed", a.Contains("\r\n") ? "CRLF" : "LF", b.Contains("\r\n") ? "CRLF" : "LF"));
                continue;
            }

            List<Diff>? found =
                IsEnv(rel) ? Env(rel, a, b)
                : StackScanner.IsCompose(rel) ? Compose(rel, a, b, await EnvOf(current, rel), await EnvOf(reference, rel))
                : rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Json(rel, a, b)
                : null;
            if (found is null) diffs.Add(Lines(rel, a, b));
            else if (found.Count == 0) diffs.Add(new Diff("file", rel, "comments or formatting only", "changed", null, null));
            else diffs.AddRange(found);
        }
        return new CompareResult(current, reference, same, diffs, notes);
    }

    private static List<Diff> Maps(string area, string file, IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        var d = new List<Diff>();
        foreach (var k in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            var (inA, inB) = (a.TryGetValue(k, out var va), b.TryGetValue(k, out var vb));
            if (inA && !inB) d.Add(new Diff(area, file, k, "added", va, null));
            else if (!inA && inB) d.Add(new Diff(area, file, k, "removed", null, vb));
            else if (va != vb) d.Add(new Diff(area, file, k, "changed", va, vb));
        }
        return d;
    }

    public static List<Diff> Env(string file, string a, string b)
    {
        static Dictionary<string, string> M(string t) =>
            Scanner.EnvFile("", t, "env-file").GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last().Value ?? "", StringComparer.Ordinal);
        return Maps("env", file, M(a), M(b));
    }

    /// Null if either side is not valid JSON (the caller then falls back to a line comparison).
    public static List<Diff>? Json(string file, string a, string b)
    {
        static Dictionary<string, string>? Flat(string text)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException) { return null; }
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            void Walk(JsonNode? n, string path)
            {
                switch (n)
                {
                    case JsonObject o: foreach (var (k, v) in o) Walk(v, path.Length == 0 ? k : $"{path}:{k}"); break;
                    // a list of plain values is a set: adding one item must not look like every later item changed
                    case JsonArray arr when arr.All(x => x is JsonValue or null):
                        foreach (var x in arr) flat[$"{path}[] {ToolContext.NodeStr(x) ?? "null"}"] = "(listed)";
                        break;
                    case JsonArray arr: for (var i = 0; i < arr.Count; i++) Walk(arr[i], $"{path}:{i}"); break;
                    default: flat[path] = ToolContext.NodeStr(n) ?? "null"; break;
                }
            }
            Walk(root, "");
            return flat;
        }
        return Flat(a) is { } fa && Flat(b) is { } fb ? Maps("settings", file, fa, fb) : null;
    }

    /// Service-level comparison of two compose files, each expanded with its own environment.
    public static List<Diff> Compose(string file, string a, string b, IReadOnlyDictionary<string, string> envA, IReadOnlyDictionary<string, string> envB)
    {
        static Dictionary<string, string> Facts(string text, IReadOnlyDictionary<string, string> env)
        {
            var f = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in ComposeLayout.Services("", text, env))
            {
                f[$"service {s.Name}"] = s.Image ?? (s.HasBuild ? "(built locally)" : "(no image)");
                if (s.Image is not null) f[$"{s.Name}.image"] = s.Image;
                if (s.ContainerName is not null) f[$"{s.Name}.container_name"] = s.ContainerName;
                foreach (var d in s.DependsOn) f[$"{s.Name} depends_on {d}"] = "(listed)";
            }
            var (ports, mounts, declared) = ComposeLayout.Parse("", text, env);
            foreach (var p in ports)
                f[$"{p.Service} {(p.Kind == "expose" ? "expose" : "port")} {(p.Kind == "expose" ? "" : $"{p.HostIp ?? "*"}:{p.Published ?? "(random)"} -> ")}{p.Target}/{p.Protocol}"] = "(listed)";
            foreach (var m in mounts)
                f[$"{m.Service} {m.Kind} {(m.Source is null ? "" : m.Source + " -> ")}{m.Target}{(m.ReadOnly ? " (ro)" : "")}"] = "(listed)";
            foreach (var v in declared) f[$"volume {v.Name}"] = v.External ? "external" : "declared";
            foreach (var r in Scanner.Compose("", text).Refs.Where(r => r.Kind == "environment"))
                f[$"{r.Service} environment {r.Name}"] = r.Value is null ? "(passed through)" : ComposeLayout.Expand(r.Value, env);
            return f;
        }

        var (fa, fb) = (Facts(a, envA), Facts(b, envB));
        var diffs = Maps("compose", file, fa, fb);
        // a whole service added or removed says it all; drop that service's individual lines
        var whole = diffs.Where(d => d.Item.StartsWith("service ") && d.Kind != "changed").Select(d => d.Item["service ".Length..]).ToHashSet();
        return diffs.Where(d => d.Item.StartsWith("service ") ? d.Kind != "changed" : !whole.Any(s => d.Item.StartsWith(s + ".") || d.Item.StartsWith(s + " "))).ToList();
    }

    /// How many lines each side has that the other does not (order-insensitive: a rough size of the change).
    public static Diff Lines(string file, string a, string b)
    {
        static List<string> L(string t) => t.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var (la, lb) = (L(a), L(b));
        var counts = lb.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var onlyHere = 0;
        foreach (var l in la) { if (counts.GetValueOrDefault(l) > 0) counts[l]--; else onlyHere++; }
        return new Diff("text", file, "lines", "changed", $"{onlyHere} line(s) only here", $"{counts.Values.Sum()} line(s) only in the reference");
    }

    /// hideSecrets: print ******** for values of secret-looking names (the spool file does; the screen does not).
    public static string Text(CompareResult r, bool hideSecrets = false)
    {
        string? H(Diff d, string? v) => v is null ? null : Hide(d, v, hideSecrets);
        var sb = new StringBuilder();
        sb.AppendLine($"compare {r.Current}").AppendLine($"against reference {r.Reference}");
        foreach (var n in r.Notes) sb.AppendLine($"note: {n}");
        sb.AppendLine($"{r.Same} file(s) identical, {r.FilesDifferent} differ, {r.FilesOnlyHere} only here, {r.FilesOnlyInReference} only in the reference");

        void List(string title, string kind)
        {
            var files = r.Diffs.Where(d => d.Area == "file" && d.Item == "" && d.Kind == kind).ToList();
            if (files.Count == 0) return;
            sb.AppendLine($"\n{title}");
            foreach (var f in files) sb.AppendLine($"  {f.File}");
        }
        List("ONLY HERE (not in the reference)", "added");
        List("ONLY IN THE REFERENCE (missing here)", "removed");

        var byFile = r.Diffs.Where(d => !(d.Area == "file" && d.Item == "")).GroupBy(d => d.File).ToList();
        if (byFile.Count > 0) sb.AppendLine($"\nDIFFERENT (here vs reference{(hideSecrets ? "; values of secret-looking names are hidden" : "")})");
        foreach (var g in byFile)
        {
            sb.AppendLine($"  {g.Key}");
            foreach (var d in g)
                sb.AppendLine(d.Area switch
                {
                    "file" => $"    {d.Item}{(d.Current is null ? "" : $": {d.Current} here, {d.Reference} in the reference")}",
                    "binary" => $"    content differs (sha256 {d.Current} here, {d.Reference} in the reference)",
                    "text" => $"    {d.Current}, {d.Reference}",
                    _ => d.Kind switch
                    {
                        "added" => $"    added    {d.Item}{(d.Current is null or "(listed)" ? "" : $" = {H(d, d.Current)}")}",
                        "removed" => $"    removed  {d.Item}{(d.Reference is null or "(listed)" ? "" : $"   (reference: {H(d, d.Reference)})")}",
                        _ => $"    changed  {d.Item}: {H(d, d.Current)}   (reference: {H(d, d.Reference)})"
                    }
                });
        }
        return sb.ToString();
    }
}
