using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

public sealed record PortBinding(string HostIp, string HostPort, string Target, string Protocol);
public sealed record MountInfo(string Type, string Source, string Destination, bool ReadWrite, string? Name);

/// A container as `docker inspect` reports it.
public sealed record ContainerInfo(string Id, string Name, string Status, bool Running, string Image, string? Project, string? Service,
                                   IReadOnlyDictionary<string, string> Env, IReadOnlyList<PortBinding> Ports, IReadOnlyList<MountInfo> Mounts);

/// One comparison of what the configuration says with what is running.
/// Area: container | image | port | mount | env. Status: ok | differs | missing (configured, not in effect) |
/// extra (in effect, not configured) | not-running | taken (the host port is held by a different container).
public sealed record RuntimeFinding(string Stack, string Service, string Area, string Item, string Status, string? Expected, string? Actual);

public sealed record RuntimeReport(IReadOnlyList<ContainerInfo> Containers, IReadOnlyList<RuntimeFinding> Findings, IReadOnlyList<string> Notes)
{
    public int Problems => Findings.Count(f => f.Status != "ok");
}

/// Answers "is the configuration actually in effect?" by tallying a deployment scan against `docker inspect`:
/// is each service's container there and running, on the configured image, publishing the configured ports (and if
/// not, which container holds the port), with the configured mounts and environment. Read-only: it only inspects.
/// It cannot say why something differs (a stale container, an edit not yet applied, a failed start); it shows that it does.
public static class RuntimeCheck
{
    public static async Task<RuntimeReport> RunAsync(IHost host, DirScan scan, CancellationToken ct)
    {
        var notes = new List<string>();
        List<ContainerInfo> containers;
        try { containers = await InspectAllAsync(host, ct); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return new RuntimeReport([], [], [$"docker is not reachable on this host: {ex.Message}"]);
        }
        return new RuntimeReport(containers, scan.Stacks.SelectMany(s => Check(s, containers)).ToList(), notes);
    }

    public static async Task<List<ContainerInfo>> InspectAllAsync(IHost host, CancellationToken ct)
    {
        var ids = await host.ExecAsync("docker", ["ps", "-a", "-q"], null, null, ct);
        if (ids.Code != 0) throw new IOException(ids.Stderr.Trim().Length > 0 ? ids.Stderr.Trim() : $"docker ps exited {ids.Code}");
        var list = ids.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (list.Count == 0) return new();
        var r = await host.ExecAsync("docker", ["inspect", .. list], null, null, ct);
        if (r.Code != 0) throw new IOException($"docker inspect failed: {r.Stderr.Trim()}");
        return Parse(r.Stdout);
    }

    /// Parses the JSON array `docker inspect` prints.
    public static List<ContainerInfo> Parse(string json)
    {
        var result = new List<ContainerInfo>();
        using var doc = JsonDocument.Parse(json);
        static string S(JsonElement e, params string[] path)
        {
            foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return ""; }
            return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : e.ToString();
        }
        static JsonElement? At(JsonElement e, params string[] path)
        {
            foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
            return e;
        }

        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (At(c, "Config", "Env") is { ValueKind: JsonValueKind.Array } envs)
                foreach (var e in envs.EnumerateArray())
                    if (e.GetString() is { } kv && kv.IndexOf('=') is > 0 and var eq) env[kv[..eq]] = kv[(eq + 1)..];

            var ports = new List<PortBinding>();
            if (At(c, "NetworkSettings", "Ports") is { ValueKind: JsonValueKind.Object } ps)
                foreach (var p in ps.EnumerateObject())
                {
                    var (target, proto) = p.Name.Split('/') is [var t, var pr] ? (t, pr) : (p.Name, "tcp");
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;                      // exposed but not published
                    foreach (var b in p.Value.EnumerateArray())
                        if (!ports.Any(x => x.HostPort == S(b, "HostPort") && x.Target == target && x.Protocol == proto))      // IPv4 + IPv6 twins
                            ports.Add(new PortBinding(S(b, "HostIp"), S(b, "HostPort"), target, proto));
                }

            var mounts = new List<MountInfo>();
            if (At(c, "Mounts") is { ValueKind: JsonValueKind.Array } ms)
                foreach (var m in ms.EnumerateArray())
                    mounts.Add(new MountInfo(S(m, "Type"), S(m, "Source"), S(m, "Destination"), S(m, "RW") != "False", S(m, "Name") is { Length: > 0 } n ? n : null));

            var labels = At(c, "Config", "Labels");
            string? Label(string k) => labels is { ValueKind: JsonValueKind.Object } l && l.TryGetProperty(k, out var v) ? v.GetString() : null;
            result.Add(new ContainerInfo(S(c, "Id") is { Length: > 12 } id ? id[..12] : S(c, "Id"), S(c, "Name").TrimStart('/'), S(c, "State", "Status"),
                S(c, "State", "Running") == "True", S(c, "Config", "Image"), Label("com.docker.compose.project"), Label("com.docker.compose.service"), env, ports, mounts));
        }
        return result;
    }

    /// Docker reports bind sources in several spellings of the same Windows path; compare them in one form.
    public static string NormalisePath(string p)
    {
        p = p.Replace('\\', '/').TrimEnd('/');
        p = Regex.Replace(p, @"^(?:/run/desktop/mnt/host|/host_mnt|/mnt)/([A-Za-z])/", "$1:/");
        p = Regex.Replace(p, @"^/([A-Za-z])/", "$1:/");
        return p.ToLowerInvariant();
    }

    public static List<RuntimeFinding> Check(Stack stack, IReadOnlyList<ContainerInfo> containers)
    {
        var findings = new List<RuntimeFinding>();
        var project = stack.Dir[(stack.Dir.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var dotEnv = stack.Variables.SelectMany(v => v.Refs).Where(r => r.Kind == "env-file").GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last().Value ?? "", StringComparer.Ordinal);

        foreach (var svc in stack.Services)
        {
            void Add(string area, string item, string status, string? expected, string? actual) =>
                findings.Add(new RuntimeFinding(stack.Dir, svc.Name, area, item, status, expected, actual));

            var mine = containers.Where(c => c.Service == svc.Name).ToList();
            var c = mine.FirstOrDefault(x => x.Project == project) ?? (svc.ContainerName is { } cn ? containers.FirstOrDefault(x => x.Name == cn) : null) ?? (mine.Count == 1 ? mine[0] : null);
            if (c is null)
            {
                Add("container", "container", "missing", "a container for this service", mine.Count > 1 ? $"{mine.Count} candidates in other projects" : "none exists");
                foreach (var p in stack.Ports.Where(p => p.Service == svc.Name && p is { Kind: "publish", Published: not null }))
                    Owner(findings, stack, svc.Name, p, null, containers);
                continue;
            }
            Add("container", c.Name, c.Running ? "ok" : "not-running", "running", c.Status);
            if (svc.Image is not null) Add("image", "image", svc.Image == c.Image ? "ok" : "differs", svc.Image, c.Image);

            // ports: configured -> in effect, then anything published that the configuration does not mention
            var configured = stack.Ports.Where(p => p.Service == svc.Name && p is { Kind: "publish", Published: not null } && !p.Published!.Contains("${")).ToList();
            foreach (var p in configured)
            {
                var label = $"{p.HostIp ?? "*"}:{p.Published} -> {p.Target}/{p.Protocol}";
                var same = c.Ports.FirstOrDefault(b => b.Target == p.Target && b.Protocol == p.Protocol && b.HostPort == p.Published);
                var other = c.Ports.FirstOrDefault(b => b.Target == p.Target && b.Protocol == p.Protocol);
                if (same is not null) Add("port", label, "ok", p.Published, same.HostPort);
                else if (other is not null) Add("port", label, "differs", $"host port {p.Published}", $"host port {other.HostPort}");
                else Add("port", label, c.Running ? "missing" : "not-running", $"host port {p.Published}", c.Running ? "not published" : "container is not running");
                Owner(findings, stack, svc.Name, p, c, containers);
            }
            foreach (var b in c.Ports.Where(b => !configured.Any(p => p.Target == b.Target && p.Protocol == b.Protocol)))
                Add("port", $"{b.HostIp}:{b.HostPort} -> {b.Target}/{b.Protocol}", "extra", null, "published, but not in the configuration");

            // mounts
            var mounts = stack.Mounts.Where(m => m.Service == svc.Name && m.Kind is "bind" or "volume").Where(m => m.Service == svc.Name).ToList();
            foreach (var m in mounts)
            {
                var actual = c.Mounts.FirstOrDefault(x => x.Destination.TrimEnd('/') == m.Target.TrimEnd('/'));
                var want = m.Kind == "bind" ? m.Resolved ?? m.Source ?? "" : m.Source ?? "";
                var label = $"{m.Kind} {m.Target}";
                if (actual is null) Add("mount", label, "missing", want, "nothing mounted there");
                else if (m.Kind == "bind")
                    Add("mount", label, actual.Type == "bind" && NormalisePath(actual.Source) == NormalisePath(want) ? "ok" : "differs", want, $"{actual.Type} {actual.Source}");
                else
                    Add("mount", label, actual.Type == "volume" && (actual.Name == want || actual.Name?.EndsWith("_" + want) == true) ? "ok" : "differs", want, $"{actual.Type} {actual.Name ?? actual.Source}");
                if (actual is not null && m.ReadOnly == actual.ReadWrite) Add("mount", label + " access", "differs", m.ReadOnly ? "read-only" : "read-write", actual.ReadWrite ? "read-write" : "read-only");
            }
            foreach (var x in c.Mounts.Where(x => x.Type == "bind" && !mounts.Any(m => m.Target.TrimEnd('/') == x.Destination.TrimEnd('/'))))
                Add("mount", $"bind {x.Destination}", "extra", null, x.Source);

            // environment: what the compose file and its env_files say the container should see
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in stack.Variables.SelectMany(v => v.Refs).Where(r => r.Service == svc.Name && r.Kind == "container-env-file")) expected[r.Name] = r.Value ?? "";
            foreach (var r in stack.Variables.SelectMany(v => v.Refs).Where(r => r.Service == svc.Name && r.Kind == "environment"))
            {
                if (r.Value is null) { if (dotEnv.TryGetValue(r.Name, out var passed)) expected[r.Name] = passed; else expected.Remove(r.Name); }
                else expected[r.Name] = ComposeLayout.Expand(r.Value, dotEnv);
            }
            foreach (var (k, v) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!c.Env.TryGetValue(k, out var actual)) Add("env", k, "missing", v, "not set in the container");
                else Add("env", k, actual == v ? "ok" : "differs", v, actual);
            }
        }

        // containers of this project that the configuration no longer mentions (e.g. a service that was removed)
        foreach (var c in containers.Where(c => c.Project == project && c.Service is not null && stack.Services.All(s => s.Name != c.Service)))
            findings.Add(new RuntimeFinding(stack.Dir, c.Service!, "container", c.Name, "extra", null, $"{c.Status}, image {c.Image}; not in the compose file"));
        return findings;
    }

    /// If a configured host port is held by some other container, say which: that is usually why a start fails.
    private static void Owner(List<RuntimeFinding> findings, Stack stack, string service, PortMap p, ContainerInfo? own, IReadOnlyList<ContainerInfo> containers)
    {
        if (p.Published is null || p.Published.Contains("${")) return;
        var holders = containers.Where(x => x.Id != own?.Id && x.Ports.Any(b => b.HostPort == p.Published && b.Protocol == p.Protocol)).ToList();
        foreach (var h in holders)
            findings.Add(new RuntimeFinding(stack.Dir, service, "port", $"host port {p.Published}/{p.Protocol}", "taken", "free for this service",
                $"held by container {h.Name}{(h.Service is null ? "" : $" (service {h.Service}, project {h.Project})")}"));
    }

    public static string Text(RuntimeReport r, string root)
    {
        var sb = new StringBuilder();
        string Rel(string p) => p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? (p[root.Length..].TrimStart('/') is { Length: > 0 } x ? x : ".") : p;
        foreach (var n in r.Notes) sb.AppendLine($"note: {n}");
        sb.AppendLine($"runtime check: {r.Containers.Count} container(s) inspected, {r.Findings.Count(f => f.Status == "ok")} setting(s) in effect, {r.Problems} not");
        foreach (var g in r.Findings.GroupBy(f => (f.Stack, f.Service)))
        {
            var bad = g.Where(f => f.Status != "ok").ToList();
            sb.AppendLine($"\n{Rel(g.Key.Stack)} / {g.Key.Service}: {g.Count(f => f.Status == "ok")} ok, {bad.Count} to look at");
            foreach (var f in bad)
                sb.AppendLine($"  {f.Status,-11} {f.Area,-9} {f.Item}{(f.Expected is null ? "" : $"   configured: {f.Expected}")}{(f.Actual is null ? "" : $"   actual: {f.Actual}")}");
        }
        return sb.ToString();
    }
}
