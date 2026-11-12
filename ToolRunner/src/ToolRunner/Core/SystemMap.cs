using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// A service on the map. Column 0 is the left edge: the services nothing depends on (entry points); each column to
/// the right holds what the previous ones depend on. Row is the position within the column.
public sealed record MapNode(string Service, string? Image, int Column, int Row, IReadOnlyList<string> Ports,
                             int Binds, int MissingBinds, int Volumes, int PluginsListed, int PluginsMissing,
                             IReadOnlyList<string> DependsOn, IReadOnlyList<string> Dependants);

/// A line on the map. Kind "depends": compose depends_on. Kind "talks": a setting of From holds To's name
/// (PLUGINHOST_HOST=pluginhost), with no depends_on between them. Via lists those settings either way.
public sealed record MapLink(string From, string To, string Kind, IReadOnlyList<string> Via);

public sealed record StackMap(string Dir, IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapLink> Links, int Columns, int Rows);

/// Turns a scanned stack into a picture's worth of facts: where each service sits and what connects them.
public static class SystemMap
{
    public static StackMap Build(Stack stack)
    {
        var services = stack.Services.GroupBy(s => s.Name).Select(g => g.First()).ToList();
        var names = services.Select(s => s.Name).ToHashSet();
        var deps = services.ToDictionary(s => s.Name, s => s.DependsOn.Where(names.Contains).Distinct().ToList());

        // connections found in configuration: a variable a service reads whose value names another service
        var via = new Dictionary<(string From, string To), List<string>>();
        foreach (var row in stack.Variables.Where(v => !string.IsNullOrEmpty(v.Value)))
            foreach (var target in names.Where(n => n.Length > 1 && Regex.IsMatch(row.Value!, $@"(^|[/@=;,\s]){Regex.Escape(n)}($|[:/;,\s])")))
                foreach (var consumer in row.Refs.Select(r => r.Service).OfType<string>().Where(c => c != target && names.Contains(c)).Distinct())
                {
                    if (!via.TryGetValue((consumer, target), out var list)) via[(consumer, target)] = list = new();
                    if (!list.Contains(row.Name)) list.Add(row.Name);
                }

        var links = new List<MapLink>();
        foreach (var (from, tos) in deps)
            foreach (var to in tos) links.Add(new MapLink(from, to, "depends", via.GetValueOrDefault((from, to)) ?? []));
        foreach (var ((from, to), settings) in via)
            if (!deps[from].Contains(to)) links.Add(new MapLink(from, to, "talks", settings));

        // depth = longest chain of links below a service; a cycle counts as depth 0 where it closes
        var depth = new Dictionary<string, int>();
        int Depth(string s, HashSet<string> path)
        {
            if (depth.TryGetValue(s, out var d)) return d;
            if (!path.Add(s)) return 0;
            d = links.Where(l => l.From == s).Select(l => 1 + Depth(l.To, path)).DefaultIfEmpty(0).Max();
            path.Remove(s);
            return depth[s] = d;
        }
        foreach (var s in names) Depth(s, new());
        var max = depth.Values.DefaultIfEmpty(0).Max();

        var nodes = new List<MapNode>();
        foreach (var column in services.GroupBy(s => max - depth[s.Name]).OrderBy(g => g.Key))
        {
            var row = 0;
            // keep a service near what points at it: order by the rows of its dependants in the columns already placed
            foreach (var s in column.OrderBy(s => nodes.Where(n => links.Any(l => l.From == n.Service && l.To == s.Name)).Select(n => (double)n.Row).DefaultIfEmpty(double.MaxValue).Average())
                                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var mounts = stack.Mounts.Where(m => m.Service == s.Name).ToList();
                var plugins = stack.Plugins.Where(p => p.Service == s.Name).ToList();
                nodes.Add(new MapNode(s.Name, s.Image, column.Key, row++,
                    stack.Ports.Where(p => p.Service == s.Name && p.Kind == "publish" && p.Published is not null).Select(p => $"{p.Published}:{p.Target}").ToList(),
                    mounts.Count(m => m.Kind == "bind"), mounts.Count(m => m.Kind == "bind" && m.Exists == false), mounts.Count(m => m.Kind == "volume"),
                    plugins.Sum(p => p.Items.Count), plugins.Sum(p => p.Missing),
                    deps[s.Name], services.Where(o => deps[o.Name].Contains(s.Name)).Select(o => o.Name).ToList()));
            }
        }
        return new StackMap(stack.Dir, nodes, links, max + 1, nodes.GroupBy(n => n.Column).Select(g => g.Count()).DefaultIfEmpty(0).Max());
    }
}

/// Browsing folders on a host, for picking one without typing its path.
public sealed record FolderListing(string Path, string? Parent, IReadOnlyList<string> Folders, IReadOnlyList<string> Roots, bool HasCompose, string? Error);

public static class Folders
{
    private static readonly string[] ComposeNames = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    public static async Task<FolderListing> ListAsync(IHost host, string? path, CancellationToken ct)
    {
        if (host is LocalHost)
        {
            var roots = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.Name.Replace('\\', '/')).ToList();
            var dir = string.IsNullOrWhiteSpace(path) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : path;
            try
            {
                var full = Path.GetFullPath(dir);
                var folders = Directory.GetDirectories(full).Select(d => Path.GetFileName(d)!).Where(n => !n.StartsWith('$'))
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
                return new FolderListing(full.Replace('\\', '/').TrimEnd('/') is { Length: > 2 } p ? p : full.Replace('\\', '/'),
                    Directory.GetParent(full)?.FullName.Replace('\\', '/'), folders, roots, ComposeNames.Any(n => File.Exists(Path.Combine(full, n))), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return new FolderListing(dir.Replace('\\', '/'), Directory.GetParent(dir)?.FullName.Replace('\\', '/'), [], roots, false, e.Message); }
        }

        var remote = string.IsNullOrWhiteSpace(path) ? "/" : path;
        var r = await host.ExecAsync("find", [remote, "-maxdepth", "1", "-mindepth", "1", "-type", "d", "-not", "-name", ".*"], null, null, ct);
        var parent = remote.TrimEnd('/') is { Length: > 0 } t && t.LastIndexOf('/') is var i and >= 0 ? (i == 0 ? "/" : t[..i]) : null;
        var compose = false;
        foreach (var n in ComposeNames) compose |= await host.ExistsAsync(host.Combine(remote, n), ct);
        return new FolderListing(remote, remote == "/" ? null : parent,
            r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l[(l.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal).ToList(),
            ["/"], compose, r.Code == 0 ? null : r.Stderr.Trim());
    }
}
