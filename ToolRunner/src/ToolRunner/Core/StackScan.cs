using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// A published (or merely exposed) port. Kind: publish | expose. Published is null when docker picks the host port.
public sealed record PortMap(string File, int Line, string? Service, string Kind, string? HostIp, string? Published, string Target, string Protocol, string Raw);

/// One mount of a service. Kind: bind | volume (named) | anonymous | tmpfs | unresolved (source still contains ${...}).
/// Resolved is the absolute host path of a bind source; Exists says whether it is there on the host (null = unknown).
public sealed record MountRef(string File, int Line, string? Service, string Kind, string? Source, string? Resolved, string Target, bool ReadOnly, string Raw, bool? Exists);

public sealed record NamedVolume(string Name, bool External, string File);

/// A service as its compose file declares it.
public sealed record ServiceInfo(string Name, string? Image, string? ContainerName, IReadOnlyList<string> DependsOn, bool HasBuild, string File, int Line);

/// Everything found for the compose file(s) in one directory (an override file belongs to the same stack).
/// AppSettings are the appsettings*.json files under the stack; Plugins compares the assemblies they list with the
/// binaries in the bind-mounted directories.
public sealed record Stack(string Dir, IReadOnlyList<string> ComposeFiles, IReadOnlyList<ScanRow> Variables,
                           IReadOnlyList<PortMap> Ports, IReadOnlyList<MountRef> Mounts, IReadOnlyList<NamedVolume> DeclaredVolumes,
                           IReadOnlyList<ServiceInfo> Services, IReadOnlyList<string> AppSettings, IReadOnlyList<PluginReport> Plugins);

/// The same host port published by more than one service. Users are "stack-dir service".
public sealed record PortConflict(string HostIp, string Published, string Protocol, IReadOnlyList<string> Users);

public sealed record DirScan(string Root, IReadOnlyList<Stack> Stacks, IReadOnlyList<PortConflict> Conflicts,
                             IReadOnlyList<string> UnreferencedEnvFiles, IReadOnlyList<string> Notes);

/// Line-based reader for the parts of a compose file that describe ports and storage. Handles the short forms
/// ("8080:80", "./data:/data:ro", "named:/data") and the long forms (`- target: 80` / `published: 8080`, `- type: bind`),
/// after expanding ${VAR} / ${VAR:-default} from the supplied environment. Like the rest of the scanner it assumes
/// conventional space-indented block YAML and does not follow anchors.
public static class ComposeLayout
{
    private static readonly Regex Braced = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-?+])([^}]*))?\}", RegexOptions.Compiled);
    private static readonly HashSet<string> LongKeys = new(StringComparer.Ordinal)
        { "target", "published", "protocol", "host_ip", "mode", "app_protocol", "type", "source", "read_only", "bind", "volume", "tmpfs", "consistency" };

    /// ${NAME} -> env value, else the :-default, else left as written (so an unresolved reference stays visible).
    public static string Expand(string s, IReadOnlyDictionary<string, string> env) =>
        Braced.Replace(s, m => env.TryGetValue(m.Groups[1].Value, out var v) ? v
                             : m.Groups[2].Value.Contains('-') ? m.Groups[3].Value : m.Value);

    public static (List<PortMap> Ports, List<MountRef> Mounts, List<NamedVolume> Declared) Parse(
        string file, string text, IReadOnlyDictionary<string, string> env)
    {
        var ports = new List<PortMap>();
        var mounts = new List<MountRef>();
        var declared = new List<NamedVolume>();

        string? service = null, block = null;
        int? svcIndent = null, attrIndent = null;
        var blockIndent = 0;
        bool inServices = false, inVolumes = false;
        Dictionary<string, string>? item = null;
        var itemLine = 0;
        var itemIndent = 0;
        int? volIndent = null;

        void Flush()      // a long-form entry ends when the next entry or key starts
        {
            if (item is null) return;
            var d = item; item = null;
            string Get(string k) => d.TryGetValue(k, out var v) ? Expand(Unquote(v), env) : "";
            if (block == "ports")
                ports.Add(new PortMap(file, itemLine, service, "publish", Get("host_ip") is { Length: > 0 } ip ? ip : null,
                    Get("published") is { Length: > 0 } pub ? pub : null, Get("target"), Get("protocol") is { Length: > 0 } pr ? pr : "tcp",
                    string.Join(", ", d.Select(kv => $"{kv.Key}: {kv.Value}"))));
            else if (block == "volumes")
            {
                var src = Get("source");
                var kind = Get("type") is { Length: > 0 } ty ? ty : Classify(src);
                mounts.Add(new MountRef(file, itemLine, service, kind is "volume" or "bind" or "tmpfs" ? kind : "unresolved", src.Length > 0 ? src : null, null,
                    Get("target"), Get("read_only") == "true", string.Join(", ", d.Select(kv => $"{kv.Key}: {kv.Value}")), null));
            }
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var indent = l.Length - l.TrimStart().Length;

            if (indent == 0)
            {
                Flush();
                (inServices, inVolumes) = (t == "services:", t == "volumes:");
                service = block = null; svcIndent = attrIndent = volIndent = null;
                continue;
            }

            if (inVolumes)      // top-level named volumes, and whether each is external
            {
                volIndent ??= indent;
                if (indent == volIndent && Regex.Match(t, @"^([\w.\-]+):") is { Success: true } vm) declared.Add(new NamedVolume(vm.Groups[1].Value, false, file));
                else if (declared.Count > 0 && Regex.IsMatch(t, @"^external:\s*(?!false\b)\S")) declared[^1] = declared[^1] with { External = true };
                continue;
            }
            if (!inServices) continue;

            svcIndent ??= indent;
            if (indent == svcIndent && Regex.Match(t, @"^([\w.\-]+):\s*(#.*)?$") is { Success: true } sm)
            {
                Flush(); service = sm.Groups[1].Value; block = null; attrIndent = null; continue;
            }
            if (service is null) continue;

            attrIndent ??= indent;
            if (indent == attrIndent)
            {
                Flush(); block = null;
                if (Regex.Match(t, @"^(ports|volumes|expose|tmpfs):\s*(.*)$") is { Success: true } bm)
                {
                    block = bm.Groups[1].Value; blockIndent = indent;
                    var inline = bm.Groups[2].Value.Trim();
                    if (inline.StartsWith('['))      // flow style: ports: ["80:80", "443:443"]
                        foreach (var v in inline.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            Scalar(block, Unquote(v), i + 1);
                }
                continue;
            }
            if (block is null || indent <= blockIndent) continue;

            if (t.StartsWith("- "))
            {
                Flush();
                var v = t[2..].Trim();
                itemIndent = indent;
                if (Regex.Match(v, @"^([A-Za-z_]+):\s*(.*)$") is { Success: true } km && LongKeys.Contains(km.Groups[1].Value))
                { item = new() { [km.Groups[1].Value] = km.Groups[2].Value }; itemLine = i + 1; }
                else Scalar(block, Unquote(v), i + 1);
            }
            else if (item is not null && indent > itemIndent && Regex.Match(t, @"^([A-Za-z_]+):\s*(.*)$") is { Success: true } kv)
                item[kv.Groups[1].Value] = kv.Groups[2].Value;
        }
        Flush();
        return (ports, mounts, declared);

        void Scalar(string kind, string raw, int line)
        {
            var v = Expand(raw, env);
            switch (kind)
            {
                case "ports": ports.Add(ParsePort(file, line, service, v, raw)); break;
                case "expose": ports.Add(new PortMap(file, line, service, "expose", null, null, v.Split('/')[0], v.Contains('/') ? v.Split('/')[1] : "tcp", raw)); break;
                case "volumes": mounts.Add(ParseMount(file, line, service, v, raw)); break;
                case "tmpfs": mounts.Add(new MountRef(file, line, service, "tmpfs", null, null, v.Split(':')[0], false, raw, null)); break;
            }
        }
    }

    /// Each service with its image, container name and what it depends on (list, flow and map forms of depends_on).
    public static List<ServiceInfo> Services(string file, string text, IReadOnlyDictionary<string, string> env)
    {
        var result = new List<ServiceInfo>();
        string? name = null, image = null, container = null;
        var deps = new List<string>();
        var hasBuild = false;
        var line = 0;
        int? svcIndent = null, attrIndent = null, depIndent = null;
        var inServices = false;
        var inDeps = false;

        void Flush()
        {
            if (name is not null) result.Add(new ServiceInfo(name, image, container, deps.ToList(), hasBuild, file, line));
            name = image = container = null; deps.Clear(); hasBuild = false; attrIndent = depIndent = null; inDeps = false;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd('\r');
            var t = l.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var indent = l.Length - l.TrimStart().Length;

            if (indent == 0) { Flush(); inServices = t == "services:"; svcIndent = null; continue; }
            if (!inServices) continue;
            svcIndent ??= indent;
            if (indent == svcIndent && Regex.Match(t, @"^([\w.\-]+):\s*(#.*)?$") is { Success: true } sm) { Flush(); name = sm.Groups[1].Value; line = i + 1; continue; }
            if (name is null) continue;

            attrIndent ??= indent;
            if (indent == attrIndent)
            {
                inDeps = false; depIndent = null;
                if (Regex.Match(t, @"^image:\s*(.+)$") is { Success: true } im) image = Expand(Unquote(im.Groups[1].Value.Trim()), env);
                else if (Regex.Match(t, @"^container_name:\s*(.+)$") is { Success: true } cm) container = Expand(Unquote(cm.Groups[1].Value.Trim()), env);
                else if (t.StartsWith("build:")) hasBuild = true;
                else if (Regex.Match(t, @"^depends_on:\s*(.*)$") is { Success: true } dm)
                {
                    inDeps = true;
                    if (dm.Groups[1].Value.Trim() is { Length: > 0 } flow && flow.StartsWith('['))
                        deps.AddRange(flow.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Unquote));
                }
                continue;
            }
            if (!inDeps) continue;
            depIndent ??= indent;
            if (indent != depIndent) continue;                                   // deeper lines are a dependency's condition
            if (t.StartsWith("- ")) deps.Add(Unquote(t[2..].Trim()));
            else if (Regex.Match(t, @"^([\w.\-]+):") is { Success: true } km) deps.Add(km.Groups[1].Value);
        }
        Flush();
        return result;
    }

    /// "[ip:]published:target[/proto]" (published and target may be ranges), or just "target".
    public static PortMap ParsePort(string file, int line, string? service, string expanded, string raw)
    {
        var v = expanded;
        var proto = "tcp";
        if (v.LastIndexOf('/') is > 0 and var slash && v[(slash + 1)..] is "tcp" or "udp" or "sctp") { proto = v[(slash + 1)..]; v = v[..slash]; }

        string? ip = null;
        if (v.StartsWith('[') && v.IndexOf(']') is > 0 and var end) { ip = v[..(end + 1)]; v = v[(end + 2)..]; }
        var parts = v.Split(':');
        if (ip is null && parts.Length == 3) { ip = parts[0]; parts = parts[1..]; }
        return parts.Length >= 2
            ? new PortMap(file, line, service, "publish", ip, parts[0].Length > 0 ? parts[0] : null, parts[1], proto, raw)
            : new PortMap(file, line, service, "publish", ip, null, parts[0], proto, raw);
    }

    /// "source:target[:opts]", or just "target" (an anonymous volume). Keeps a Windows drive letter in the source.
    public static MountRef ParseMount(string file, int line, string? service, string expanded, string raw)
    {
        var parts = expanded.Split(':').ToList();
        // C:\data[:/target] or C:/data:/target; but "v:/v" is a named volume called v (a drive needs a backslash, or a further absolute target)
        if (parts.Count > 1 && parts[0].Length == 1 && char.IsLetter(parts[0][0]) &&
            (parts[1].StartsWith('\\') || (parts[1].StartsWith('/') && parts.Count > 2 && (parts[2].StartsWith('/') || parts[2].StartsWith('\\')))))
        { parts[0] = parts[0] + ":" + parts[1]; parts.RemoveAt(1); }

        if (parts.Count == 1) return new MountRef(file, line, service, "anonymous", null, null, parts[0], false, raw, null);
        var ro = parts.Count > 2 && parts[2].Split(',').Contains("ro");
        return new MountRef(file, line, service, Classify(parts[0]), parts[0], null, parts[1], ro, raw, null);
    }

    /// A source that looks like a path is a bind mount; a plain name is a named volume.
    public static string Classify(string source) =>
        source.Contains("${") ? "unresolved"
        : source.Length == 0 ? "unresolved"
        : source[0] is '.' or '/' or '~' || source.Contains('/') || source.Contains('\\') || Regex.IsMatch(source, @"^[A-Za-z]:") ? "bind"
        : "volume";

    private static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s[1..^1] : s;

    /// Joins and collapses "." and ".." so the same bind source compares equal however it was written.
    public static string Collapse(string path)
    {
        var o = new List<string>();
        foreach (var s in path.Replace('\\', '/').Split('/'))
        {
            if (s == ".") continue;
            if (s == ".." && o.Count == 1 && o[0] == "") continue;                       // above the root is the root
            if (s == ".." && o.Count > 0 && o[^1] is not ".." and not "" && !(o.Count == 1 && o[0].EndsWith(':'))) { o.RemoveAt(o.Count - 1); continue; }
            o.Add(s);
        }
        return string.Join('/', o);
    }
}

/// Scans a directory tree (on the local machine or over ssh) for compose files and env files, and summarises variables,
/// ports and storage per stack, plus port conflicts and bind sources that do not exist.
public static class StackScanner
{
    private static readonly string[] ComposePatterns =
        { "docker-compose*.yml", "docker-compose*.yaml", "compose.yml", "compose.yaml", "compose.*.yml", "compose.*.yaml" };
    private static readonly string[] EnvPatterns = { ".env", "*.env", ".env.*" };

    private const string AppSettingsPattern = "appsettings*.json";
    private static bool IsAppSettings(string path) => FileSystemName.MatchesSimpleExpression(AppSettingsPattern, path[(path.LastIndexOf('/') + 1)..], true);

    internal static bool IsCompose(string path) =>
        ComposePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, path[(path.LastIndexOf('/') + 1)..], true));

    private static string DirOf(string path) => path.LastIndexOf('/') is > 0 and var i ? path[..i] : ".";

    public static async Task<DirScan> ScanDirectoryAsync(IHost host, string root, int maxDepth, CancellationToken ct)
    {
        var notes = new List<string>();
        var files = await host.FindFilesAsync(root, [.. ComposePatterns, .. EnvPatterns, AppSettingsPattern], maxDepth, ct);
        var composeFiles = files.Where(IsCompose).ToList();
        var appSettings = files.Where(IsAppSettings).ToList();
        var envFiles = files.Where(f => !IsCompose(f) && !IsAppSettings(f)).ToHashSet();
        // an appsettings file belongs to the nearest stack directory above it
        var stackDirs = composeFiles.Select(DirOf).Distinct().OrderByDescending(d => d.Length).ToList();
        string? OwnerOf(string f) => stackDirs.FirstOrDefault(d => f.StartsWith(d + "/", StringComparison.Ordinal));
        if (composeFiles.Count == 0) notes.Add($"no compose files found under {root} (depth {maxDepth})");

        var referenced = new HashSet<string>();
        var stacks = new List<Stack>();
        foreach (var group in composeFiles.GroupBy(DirOf).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var dir = group.Key;
            var refs = new List<VarRef>();
            var envFileRefs = new List<(string Path, string? Service)>();
            var texts = new List<(string File, string Text)>();

            foreach (var f in group)
            {
                var text = await host.ReadTextAsync(f, ct);
                if (text is null) { notes.Add($"could not read {f}"); continue; }
                texts.Add((f, text));
                var (r, ef) = Scanner.Compose(f, text);
                refs.AddRange(r); envFileRefs.AddRange(ef);
            }

            // the project env file next to the compose file feeds ${} interpolation
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            var dotEnv = dir + "/.env";
            if (await host.ReadTextAsync(dotEnv, ct) is { } dotText)
            {
                referenced.Add(dotEnv);
                var e = Scanner.EnvFile(dotEnv, dotText, "env-file");
                refs.AddRange(e);
                foreach (var r in e) env[r.Name] = r.Value ?? "";
            }
            foreach (var (path, svc) in envFileRefs.Distinct())
            {
                var full = path.StartsWith('/') || Regex.IsMatch(path, @"^[A-Za-z]:") ? path : ComposeLayout.Collapse(dir + "/" + path);
                referenced.Add(full);
                if (await host.ReadTextAsync(full, ct) is { } et) refs.AddRange(Scanner.EnvFile(full, et, "container-env-file", svc));
                else notes.Add($"env_file of '{svc}' not found: {full}");
            }

            var ports = new List<PortMap>();
            var mounts = new List<MountRef>();
            var declared = new List<NamedVolume>();
            var services = new List<ServiceInfo>();
            foreach (var (f, text) in texts)
            {
                var (p, m, d) = ComposeLayout.Parse(f, text, env);
                ports.AddRange(p); declared.AddRange(d);
                foreach (var mount in m) mounts.Add(await Resolve(host, dir, mount, ct));
                services.AddRange(ComposeLayout.Services(f, text, env));
            }

            var mine = appSettings.Where(a => OwnerOf(a) == dir).ToList();
            var plugins = new List<PluginReport>();
            foreach (var a in mine)
            {
                // the service that will read this file is the one that bind-mounts it (or a directory above it)
                var binds = mounts.Where(m => m is { Kind: "bind", Resolved: not null }).ToList();
                var owner = binds.FirstOrDefault(m => a == m.Resolved || a.StartsWith(m.Resolved + "/", StringComparison.Ordinal))?.Service;
                var dirs = binds.Where(m => owner is null || m.Service == owner).Where(m => m.Exists == true).Select(m => m.Resolved!).Distinct().ToList();
                try
                {
                    var report = await PluginCheck.CheckAsync(host, a, owner, dirs, null, ct);
                    if (report.Items.Count > 0) plugins.Add(report);
                }
                catch (IOException ex) { notes.Add(ex.Message); }
            }
            stacks.Add(new Stack(dir, group.ToList(), Scanner.Merge(refs), ports, mounts, declared, services, mine, plugins));
        }

        return new DirScan(root, stacks, Conflicts(stacks), envFiles.Except(referenced).Order(StringComparer.Ordinal).ToList(), notes);
    }

    /// For a bind mount: the absolute source on the host, and whether it exists there.
    private static async Task<MountRef> Resolve(IHost host, string dir, MountRef m, CancellationToken ct)
    {
        if (m.Kind != "bind" || m.Source is null) return m;
        var abs = m.Source.StartsWith('~') || m.Source.StartsWith('/') || Regex.IsMatch(m.Source, @"^[A-Za-z]:")
            ? ComposeLayout.Collapse(m.Source) : ComposeLayout.Collapse(dir + "/" + m.Source);
        bool? exists = m.Source.StartsWith('~') ? null : await host.ExistsAsync(abs, ct);      // ~ is expanded by a shell we are not in
        return m with { Resolved = abs, Exists = exists };
    }

    public static List<PortConflict> Conflicts(IEnumerable<Stack> stacks) =>
        stacks.SelectMany(s => s.Ports.Where(p => p.Kind == "publish" && p.Published is not null && !p.Published.Contains("${"))
                                       .Select(p => (Key: (Ip: p.HostIp ?? "*", p.Published, p.Protocol), User: $"{s.Dir} {p.Service ?? "?"}")))
              .GroupBy(x => x.Key)
              .Where(g => g.Select(x => x.User).Distinct().Count() > 1)
              .Select(g => new PortConflict(g.Key.Ip, g.Key.Published!, g.Key.Protocol, g.Select(x => x.User).Distinct().ToList()))
              .OrderBy(c => c.Published, StringComparer.Ordinal).ToList();

    /// A fixed-width plain-text summary, for the terminal, the spool file and the UI's copy button.
    public static string Text(DirScan s)
    {
        var sb = new StringBuilder();
        string Rel(string p) => p.StartsWith(s.Root, StringComparison.OrdinalIgnoreCase) ? (p[s.Root.Length..].TrimStart('/') is { Length: > 0 } r ? r : ".") : p;
        var ports = s.Stacks.SelectMany(x => x.Ports.Select(p => (Stack: x, Port: p))).ToList();
        var mounts = s.Stacks.SelectMany(x => x.Mounts.Select(m => (Stack: x, Mount: m))).ToList();
        sb.AppendLine($"scan of {s.Root}: {s.Stacks.Count} stack(s), {ports.Count(p => p.Port.Kind == "publish")} published port(s), " +
                      $"{mounts.Count(m => m.Mount.Kind == "bind")} bind mount(s), {mounts.Count(m => m.Mount.Kind == "volume")} named volume mount(s)");

        sb.AppendLine("\nPORTS (host -> container)");
        foreach (var (st, p) in ports.OrderBy(x => x.Port.Published is null).ThenBy(x => int.TryParse(x.Port.Published, out var n) ? n : int.MaxValue).ThenBy(x => x.Stack.Dir))
            sb.AppendLine($"  {(p.Kind == "expose" ? "(internal)" : $"{p.HostIp ?? "*"}:{p.Published ?? "(random)"}"),-22} -> {$"{p.Target}/{p.Protocol}",-10} {p.Service,-12} {Rel(st.Dir)}  {Path.GetFileName(p.File)}:{p.Line}");
        if (s.Conflicts.Count > 0)
        {
            sb.AppendLine("\nPORT CONFLICTS (same host port published by more than one service)");
            foreach (var c in s.Conflicts) sb.AppendLine($"  {c.HostIp}:{c.Published}/{c.Protocol}  <- {string.Join("; ", c.Users.Select(Rel))}");
        }

        sb.AppendLine("\nNAMED VOLUMES");
        foreach (var (st, m) in mounts.Where(x => x.Mount.Kind == "volume"))
            sb.AppendLine($"  {m.Source,-20} -> {m.Target}{(m.ReadOnly ? " (ro)" : "")}  {m.Service}  {Rel(st.Dir)}{(st.DeclaredVolumes.FirstOrDefault(v => v.Name == m.Source) is { External: true } ? "  [external]" : st.DeclaredVolumes.Any(v => v.Name == m.Source) ? "" : "  [NOT DECLARED]")}");
        foreach (var (st, m) in mounts.Where(x => x.Mount.Kind is "anonymous" or "tmpfs"))
            sb.AppendLine($"  ({m.Kind}) -> {m.Target}  {m.Service}  {Rel(st.Dir)}");

        sb.AppendLine("\nBIND MOUNTS (host path -> container path)");
        foreach (var (st, m) in mounts.Where(x => x.Mount.Kind == "bind"))
            sb.AppendLine($"  {Rel(m.Resolved ?? m.Source ?? "")} -> {m.Target}{(m.ReadOnly ? " (ro)" : "")}  {m.Service}  {(m.Exists switch { true => "exists", false => "MISSING", _ => "unchecked" })}");
        foreach (var (st, m) in mounts.Where(x => x.Mount.Kind == "unresolved"))
            sb.AppendLine($"  ?{m.Source} -> {m.Target}  {m.Service}  (source still has ${{...}}; define it to classify)");

        var shared = mounts.Where(x => x.Mount is { Kind: "bind", Resolved: not null }).GroupBy(x => x.Mount.Resolved!)
            .Where(g => g.Select(x => $"{x.Stack.Dir} {x.Mount.Service}").Distinct().Count() > 1).ToList();
        if (shared.Count > 0)
        {
            sb.AppendLine("\nSHARED BIND SOURCES (the same host path mounted by more than one service)");
            foreach (var g in shared) sb.AppendLine($"  {Rel(g.Key)}  <- {string.Join("; ", g.Select(x => $"{x.Mount.Service} -> {x.Mount.Target}").Distinct())}");
        }

        var reports = s.Stacks.SelectMany(x => x.Plugins).ToList();
        if (reports.Count > 0)
        {
            sb.AppendLine("\nPLUGIN ASSEMBLIES (names listed in appsettings vs .dll files in the bind-mounted directories)");
            foreach (var r in reports)
            {
                sb.AppendLine($"  {Rel(r.AppSettings)}  service: {r.Service ?? "(not bind-mounted by a service)"}  {r.Items.Count - r.Missing}/{r.Items.Count} found, {r.Unlisted.Count} unlisted");
                foreach (var i in r.Items) sb.AppendLine($"    {(i.FoundAt is null ? "MISSING" : "found  ")} {i.Name}  ({i.JsonPath}){(i.FoundAt is null ? "" : "  " + Rel(i.FoundAt))}");
                foreach (var u in r.Unlisted.Take(10)) sb.AppendLine($"    unlisted {Rel(u)}");
            }
        }

        sb.AppendLine("\nSERVICES");
        foreach (var st in s.Stacks)
            foreach (var v in st.Services)
                sb.AppendLine($"  {v.Name,-14} {v.Image ?? (v.HasBuild ? "(built locally)" : "(no image)")}{(v.DependsOn.Count > 0 ? "  depends on: " + string.Join(", ", v.DependsOn) : "")}  {Rel(st.Dir)}");

        sb.AppendLine("\nVARIABLES NEEDING ATTENTION");
        foreach (var st in s.Stacks)
            foreach (var r in st.Variables.Where(r => r.Status is "undefined" or "unused"))
                sb.AppendLine($"  {r.Status,-10} {r.Name}  ({Rel(st.Dir)})");
        foreach (var f in s.UnreferencedEnvFiles) sb.AppendLine($"  env file not used by any compose file found: {f}");
        foreach (var n in s.Notes) sb.AppendLine($"  note: {n}");
        return sb.ToString();
    }
}
