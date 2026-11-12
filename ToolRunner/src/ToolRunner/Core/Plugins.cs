using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// One assembly an appsettings file names, and where its file was found on the host (null = missing).
public sealed record PluginItem(string Name, string JsonPath, string? FoundAt);

/// The result of comparing the assemblies an appsettings file lists with the files in a service's bind-mounted directories.
/// Unlisted are .dll files sitting beside found plugins that the appsettings file does not mention.
public sealed record PluginReport(string AppSettings, string? Service, IReadOnlyList<string> SearchedDirs,
                                  IReadOnlyList<PluginItem> Items, IReadOnlyList<string> Unlisted)
{
    public int Missing => Items.Count(i => i.FoundAt is null);
}

/// Finds assembly names in an appsettings file and checks the binaries exist where the container will look.
/// With no `key`, names are found by heuristic: any string ending in .dll, plus name-like strings under a key that
/// mentions plugin/assembly/module (an assembly-qualified type "Ns.Type, Acme.Plugin" yields Acme.Plugin).
/// With a `key` (config path such as "Plugins:Assemblies"), every string under it is taken as a name.
public static class PluginCheck
{
    private static readonly Regex KeyHint = new("plugin|assembl|module|dll", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeafHint = new("assembl|dll", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameLike = new(@"^[A-Za-z_][\w.\-]*$", RegexOptions.Compiled);

    public static List<(string Name, string JsonPath)> AssemblyNames(string json, string? key = null)
    {
        var found = new List<(string, string)>();
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { return found; }

        void Add(string name, string path)
        {
            name = name.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name += ".dll";
            if (!found.Any(f => f.Item1.Equals(name, StringComparison.OrdinalIgnoreCase))) found.Add((name, path));
        }

        void Walk(JsonNode? n, List<string> path)
        {
            switch (n)
            {
                case JsonObject o: foreach (var (k, v) in o) { path.Add(k); Walk(v, path); path.RemoveAt(path.Count - 1); } break;
                case JsonArray a: for (var i = 0; i < a.Count; i++) { path.Add(i.ToString()); Walk(a[i], path); path.RemoveAt(path.Count - 1); } break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    var s = v.GetValue<string>().Trim();
                    var p = string.Join(':', path);
                    if (key is not null)
                    {
                        if ((p + ":").StartsWith(key.TrimEnd(':') + ":", StringComparison.OrdinalIgnoreCase) && s.Length > 0) Add(s, p);
                        break;
                    }
                    if (s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { Add(s, p); break; }
                    var named = path.Where(x => !int.TryParse(x, out _)).ToList();      // any key on the way down may be the hint
                    if (!named.Any(KeyHint.IsMatch)) break;
                    if (s.Split(',') is { Length: 2 } q && NameLike.IsMatch(q[0].Trim()) && NameLike.IsMatch(q[1].Trim())) { Add(q[1].Trim(), p); break; }
                    var leaf = named.Count > 0 ? named[^1] : "";
                    if (NameLike.IsMatch(s) && (s.Contains('.') || LeafHint.IsMatch(leaf))) Add(s, p);
                    break;
            }
        }
        Walk(root, new List<string>());
        return found;
    }

    public static async Task<PluginReport> CheckAsync(IHost host, string appsettings, string? service, IReadOnlyList<string> dirs, string? key, CancellationToken ct)
    {
        var text = await host.ReadTextAsync(appsettings, ct) ?? throw new IOException($"appsettings file not found: {appsettings}");
        var names = AssemblyNames(text, key);

        var files = new List<string>();
        foreach (var d in dirs.Distinct()) files.AddRange(await host.FindFilesAsync(d, ["*.dll"], 4, ct));
        string File_(string path) => path[(path.LastIndexOf('/') + 1)..];
        var byName = files.GroupBy(File_, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var items = names.Select(n => new PluginItem(n.Name, n.JsonPath, byName.GetValueOrDefault(n.Name))).ToList();
        var pluginDirs = items.Where(i => i.FoundAt is not null).Select(i => i.FoundAt![..i.FoundAt!.LastIndexOf('/')]).ToHashSet();
        var listed = names.Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlisted = files.Where(f => pluginDirs.Contains(f[..f.LastIndexOf('/')]) && !listed.Contains(File_(f))).Take(50).ToList();
        return new PluginReport(appsettings, service, dirs.Distinct().ToList(), items, unlisted);
    }
}
