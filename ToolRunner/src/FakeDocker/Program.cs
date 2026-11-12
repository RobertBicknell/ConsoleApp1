using System.Text.Json;
using System.Text.RegularExpressions;

// A stand-in for the docker CLI, for testing toolrunner without Docker. Implements only what toolrunner calls.
// State (containers, images) lives in a JSON file: %FAKEDOCKER_STATE%, default <temp>/fakedocker-state.json.
// Every invocation is appended to <state>.calls.log so you can see exactly what toolrunner ran.
// Compose files are read line-based: service names under `services:`; commented-out services do not exist.

var statePath = Environment.GetEnvironmentVariable("FAKEDOCKER_STATE") ?? Path.Combine(Path.GetTempPath(), "fakedocker-state.json");
File.AppendAllText(statePath + ".calls.log", $"{DateTime.UtcNow:O} docker {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}\n");

var st = File.Exists(statePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(statePath))! : new State();
int Done(int code) { File.WriteAllText(statePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true })); return code; }
int Err(string m) { Console.Error.WriteLine(m); return Done(1); }

string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
string? Opt(IList<string> a, string name) { var i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
List<string> Opts(IList<string> a, string name) => a.Select((v, i) => (v, i)).Where(x => x.v == name && x.i + 1 < a.Count).Select(x => a[x.i + 1]).ToList();

if (args.Length == 0) return Err("fakedocker: no command");

switch (args[0])
{
    case "--version": Console.WriteLine("Docker version 0.0.0-fake, build fake"); return Done(0);
    case "compose" when args.Length > 1 && args[1] == "version": Console.WriteLine("Docker Compose version v0.0.0-fake"); return Done(0);
    case "compose": return Compose(args.Skip(1).ToList());
    case "ps": return Ps(args.ToList());
    case "inspect": return Inspect(args.ToList());
    case "logs": return Logs(args.ToList());
    case "images":
    case "image" when args.Length > 1 && args[1] == "ls":
        foreach (var i in st.Images.Keys.Concat(st.Containers.Select(c => c.Image).Where(i => i.Length > 0)).Distinct()) Console.WriteLine(i);
        return Done(0);
    case "image" when args.Length > 1 && args[1] == "inspect":
        var img = args[^1];
        if (!st.Images.TryGetValue(img, out var labels)) return Err($"Error: No such image: {img}");
        Console.WriteLine(labels.GetValueOrDefault("org.opencontainers.image.revision", ""));
        return Done(0);
    case "build":
        var tags = Opts(args, "-t");
        var lab = Opts(args, "--label").Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
        foreach (var t in tags) st.Images[t] = lab;
        Console.WriteLine($"fake build of {string.Join(',', tags)}");
        return Done(0);
    case "events": return Done(0);   // no events
    case "history": Console.WriteLine("fake history"); return Done(0);
    case "run": Console.WriteLine("fake run"); return Done(0);
    default: return Err($"fakedocker: unsupported command '{args[0]}'");
}

List<string> ServicesIn(string composeFile)
{
    var lines = File.ReadAllLines(composeFile);
    var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^services:\s*$"));
    if (start < 0) throw new InvalidOperationException("no top-level 'services:'");
    var names = new List<string>();
    int? indent = null;
    for (var i = start + 1; i < lines.Length; i++)
    {
        var l = lines[i];
        if (l.Trim().Length == 0 || l.TrimStart().StartsWith('#')) continue;
        if (!char.IsWhiteSpace(l[0])) break;
        var ind = l.Length - l.TrimStart().Length;
        indent ??= ind;
        if (ind == indent && Regex.Match(l, @"^\s+([\w.\-]+):\s*$") is { Success: true } m) names.Add(m.Groups[1].Value);
    }
    return names;
}

IEnumerable<string> DependsOnTargets(List<string> text)
{
    for (var i = 0; i < text.Count; i++)
        if (text[i].Trim().StartsWith("depends_on:"))
        {
            var ind = text[i].Length - text[i].TrimStart().Length;
            for (var j = i + 1; j < text.Count && (text[j].Length - text[j].TrimStart().Length) > ind; j++)
                if (Regex.Match(text[j], @"^\s+(?:-\s+)?([\w.\-]+):?\s*$") is { Success: true } m) yield return m.Groups[1].Value;
        }
}

// a depends_on target that does not exist => the error real compose gives
string? Validate(string composeFile, List<string> services)
{
    var text = File.ReadAllLines(composeFile).Where(l => !l.TrimStart().StartsWith('#')).ToList();
    var missing = DependsOnTargets(text).FirstOrDefault(t => !services.Contains(t));
    return missing is null ? null : $"service \"{missing}\" depends on undefined service \"{missing}\"";
}

Dictionary<string, string> ReadEnv(string path) =>
    File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith('#') && l.Contains('='))
        .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);

int Compose(List<string> a)
{
    var projDir = Opt(a, "--project-directory") ?? ".";
    var project = Opt(a, "-p") ?? Path.GetFileName(Path.GetFullPath(projDir).TrimEnd('\\', '/')).ToLowerInvariant();
    var envFile = Opt(a, "--env-file") ?? Path.Combine(projDir, ".env");
    var files = Opts(a, "-f"); if (files.Count == 0) files.Add(Path.Combine(projDir, "docker-compose.yml"));
    foreach (var f in files) if (!File.Exists(f)) return Err($"open {f}: no such file");
    if (!File.Exists(envFile)) return Err($"Couldn't find env file: {envFile}");

    var verbIdx = a.FindIndex(x => x is "up" or "down" or "stop" or "start" or "restart" or "ps" or "config");
    if (verbIdx < 0) return Err("fakedocker: unsupported compose verb");
    var verb = a[verbIdx];
    var positional = a.Skip(verbIdx + 1).Where(x => !x.StartsWith('-')).ToList();
    var all = ServicesIn(files[0]);
    if (Validate(files[0], all) is { } bad) return Err(bad);

    switch (verb)
    {
        case "config": if (!a.Contains("-q")) foreach (var s in all) Console.WriteLine($"service: {s}"); return Done(0);
        case "ps": foreach (var c in st.Containers.Where(c => c.Project == project)) Console.WriteLine($"{c.Id}  {c.Service}  {(c.Running ? "running" : "exited")}"); return Done(0);
        case "up":
            foreach (var s in positional.Count > 0 ? positional : all)
            {
                if (!all.Contains(s)) return Err($"no such service: {s}");
                st.Containers.RemoveAll(c => c.Project == project && c.Service == s);
                var c = new Container { Id = Guid.NewGuid().ToString("N")[..12], Project = project, Service = s, Running = true, Started = Now(), EnvFile = envFile, Env = ReadEnv(envFile) };
                foreach (var f in files) Describe(c, f, projDir);      // later files override: enough for `image:`
                st.Containers.Add(c);
                Console.WriteLine($" Container {project}-{s}-1  Started");
            }
            return Done(0);
        case "stop": case "start": case "restart":
            foreach (var c in st.Containers.Where(c => c.Project == project && (positional.Count == 0 || positional.Contains(c.Service))))
            { c.Running = verb != "stop"; Console.WriteLine($" Container {project}-{c.Service}-1  {verb}"); }
            return Done(0);
        case "down": st.Containers.RemoveAll(c => c.Project == project); return Done(0);
    }
    return Done(0);
}

// What a real container would report for this service: image, published ports, mounts and environment, read from the
// compose file (short forms, plus the long form of a port) with ${VAR} / ${VAR:-default} expanded from the env file.
void Describe(Container c, string composeFile, string projDir)
{
    string Expand(string s) => Regex.Replace(s, @"\$\{([A-Za-z_]\w*)(?::?-([^}]*))?\}", m => c.Env.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Groups[2].Value);
    string Unq(string s) => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s[1..^1] : s;
    var root = Path.GetFullPath(projDir).Replace('\\', '/');

    var lines = File.ReadAllLines(composeFile).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
    // the service header is at the indent of the first entry under services: (a same-named key deeper down, e.g. in depends_on, is not it)
    var top = lines.FindIndex(l => Regex.IsMatch(l, @"^services:\s*$"));
    if (top < 0 || top + 1 >= lines.Count) return;
    var svcIndent = lines[top + 1].Length - lines[top + 1].TrimStart().Length;
    var start = lines.FindIndex(top + 1, l => l.Length - l.TrimStart().Length == svcIndent && Regex.IsMatch(l, $@"^\s+{Regex.Escape(c.Service)}:\s*$"));
    if (start < 0) return;
    var body = lines.Skip(start + 1).TakeWhile(l => l.Length - l.TrimStart().Length > svcIndent).ToList();
    if (body.Count == 0) return;
    var attrIndent = body[0].Length - body[0].TrimStart().Length;

    void Port(string v)
    {
        var proto = "tcp";
        if (v.Contains('/')) { proto = v[(v.LastIndexOf('/') + 1)..]; v = v[..v.LastIndexOf('/')]; }
        var p = v.Split(':');
        if (p.Length == 1) return;                                    // not published to a fixed host port
        c.Ports.Add(new[] { p.Length == 3 ? p[0] : "0.0.0.0", p[^2], p[^1], proto });
    }
    void Mount(string v)
    {
        var p = v.Split(':').ToList();
        if (p.Count > 1 && p[0].Length == 1 && char.IsLetter(p[0][0]) && (p[1].StartsWith('\\') || p[1].StartsWith('/')) && p.Count > 2) { p[0] += ":" + p[1]; p.RemoveAt(1); }
        if (p.Count == 1) return;
        var bind = p[0].StartsWith('.') || p[0].StartsWith('/') || p[0].StartsWith('~') || p[0].Contains(':');
        var source = bind ? (p[0].StartsWith('.') ? Path.GetFullPath(Path.Combine(root, p[0])).Replace('\\', '/') : p[0]) : $"/var/lib/docker/volumes/{c.Project}_{p[0]}/_data";
        c.Mounts.Add(new[] { bind ? "bind" : "volume", source, p[1], p.Count > 2 && p[2].Contains("ro") ? "ro" : "rw", bind ? "" : $"{c.Project}_{p[0]}" });
    }
    void EnvFile(string f) { var path = Path.IsPathRooted(f) ? f : Path.Combine(root, f); if (File.Exists(path)) foreach (var (k, v) in ReadEnv(path)) c.ContainerEnv[k] = v; }

    string? block = null;
    Dictionary<string, string>? longPort = null;
    void FlushPort() { if (longPort is { } lp && lp.TryGetValue("target", out var t) && lp.TryGetValue("published", out var pub)) c.Ports.Add(new[] { lp.GetValueOrDefault("host_ip", "0.0.0.0"), pub, t, lp.GetValueOrDefault("protocol", "tcp") }); longPort = null; }
    var pendingEnv = new List<(string, string)>();
    foreach (var l in body)
    {
        var t = l.Trim();
        var indent = l.Length - l.TrimStart().Length;
        if (indent == attrIndent)
        {
            FlushPort(); block = null;
            var m = Regex.Match(t, @"^([\w_]+):\s*(.*)$");
            if (!m.Success) continue;
            var (key, val) = (m.Groups[1].Value, m.Groups[2].Value.Trim());
            if (key == "image") c.Image = Expand(Unq(val));
            else if (key == "container_name") c.Name = Expand(Unq(val));
            else if (key is "ports" or "volumes" or "environment" or "env_file")
            {
                block = key;
                if (val.StartsWith('[')) foreach (var x in val.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { if (key == "ports") Port(Expand(Unq(x))); else if (key == "volumes") Mount(Expand(Unq(x))); }
                else if (val.Length > 0 && key == "env_file") EnvFile(Unq(val));
            }
            continue;
        }
        if (block is null) continue;
        var item = t.StartsWith("- ") ? t[2..].Trim() : t;
        switch (block)
        {
            case "ports":
                if (t.StartsWith("- ")) { FlushPort(); if (Regex.IsMatch(item, @"^(target|published|protocol|host_ip|mode):")) longPort = new(); else { Port(Expand(Unq(item))); break; } }
                if (longPort is not null && Regex.Match(item, @"^(\w+):\s*(.+)$") is { Success: true } lm) longPort[lm.Groups[1].Value] = Expand(Unq(lm.Groups[2].Value.Trim()));
                break;
            case "volumes": if (t.StartsWith("- ") && !Regex.IsMatch(item, @"^(type|source|target|read_only):")) Mount(Expand(Unq(item))); break;
            case "env_file": if (t.StartsWith("- ")) EnvFile(Unq(item)); break;
            case "environment":
                if (Regex.Match(item, @"^([A-Za-z_]\w*)\s*(?:[=:]\s*(.*))?$") is { Success: true } em)
                    pendingEnv.Add((em.Groups[1].Value, em.Groups[2].Success ? Expand(Unq(em.Groups[2].Value.Trim())) : c.Env.GetValueOrDefault(em.Groups[1].Value, "")));
                break;
        }
    }
    FlushPort();
    foreach (var (k, v) in pendingEnv) c.ContainerEnv[k] = v;      // environment: wins over env_file
}

int Inspect(List<string> a)
{
    var wanted = a.Skip(1).Where((x, i) => !x.StartsWith('-') && a[i] != "--format").ToList();
    var found = wanted.Select(w => st.Containers.FirstOrDefault(c => c.Id == w || c.Service == w || Name(c) == w)).ToList();
    if (found.Any(c => c is null)) return Err($"Error: No such object: {wanted[found.IndexOf(null)]}");
    Console.WriteLine(JsonSerializer.Serialize(found.Select(c => new
    {
        Id = c!.Id + new string('0', 52),
        Name = "/" + Name(c),
        State = new { Status = c.Running ? "running" : "exited", c.Running },
        Config = new
        {
            c.Image,
            Env = c.ContainerEnv.Select(e => $"{e.Key}={e.Value}").Append("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin").ToArray(),
            Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = c.Project, ["com.docker.compose.service"] = c.Service }
        },
        // a stopped container reports no published ports, like the real thing
        NetworkSettings = new { Ports = (c.Running ? c.Ports : new()).GroupBy(p => $"{p[2]}/{p[3]}").ToDictionary(g => g.Key, g => g.Select(p => new { HostIp = p[0], HostPort = p[1] }).ToArray()) },
        Mounts = c.Mounts.Select(m => new { Type = m[0], Source = m[1], Destination = m[2], RW = m[3] == "rw", Name = m[4] }).ToArray()
    }), new JsonSerializerOptions { WriteIndented = true }));
    return Done(0);
}

string Name(Container c) => c.Name.Length > 0 ? c.Name : $"{c.Project}-{c.Service}-1";

int Ps(List<string> a)
{
    var filters = Opts(a, "--filter");
    var svc = filters.Select(f => Regex.Match(f, @"compose\.service=(.+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
    var proj = filters.Select(f => Regex.Match(f, @"compose\.project=(.+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
    foreach (var c in st.Containers.Where(c => (svc is null || c.Service == svc) && (proj is null || c.Project == proj)))
        Console.WriteLine(a.Contains("-q") ? c.Id : Opt(a, "--format") == "{{.Names}}" ? $"{c.Project}-{c.Service}-1" : $"{c.Id}  {c.Service}");
    return Done(0);
}

// Logs: a start line, plus "Connected to <X_HOST>:<X_PORT>" for each *_HOST/*_PORT pair in the container's env file.
int Logs(List<string> a)
{
    var c = st.Containers.FirstOrDefault(c => c.Id == a[^1] || c.Service == a[^1] || $"{c.Project}-{c.Service}-1" == a[^1]);
    if (c is null) return Err($"Error: No such container: {a[^1]}");
    var lines = new List<string> { $"{c.Started} {c.Service} starting (env-file: {c.EnvFile})" };
    foreach (var h in c.Env.Keys.Where(k => k.EndsWith("_HOST")))
    {
        var port = c.Env.GetValueOrDefault(h[..^5] + "_PORT");
        if (port != null) lines.Add($"{c.Started} {c.Service} Connected to {c.Env[h]}:{port}");
    }
    // A realistic amount of noise (deterministic): heartbeats and requests, a few retries, one failure with a stack trace.
    for (var n = 1; n <= 120; n++)
    {
        lines.Add($"{c.Started} info: {c.Service}.Health[0] Health check {1000 + n} completed in {n % 17 + 3}ms");
        if (n % 4 == 0) lines.Add($"{c.Started} info: {c.Service}.Http[1] GET /api/items/{n * 7} responded 200 in {n % 31}.{n % 10}ms");
        if (n % 40 == 0) lines.Add($"{c.Started} warn: {c.Service}.Db[12] Retry {n / 40} of 5 connecting to database after {n * 25}ms");
        if (n == 77)
        {
            lines.Add($"{c.Started} fail: {c.Service}.Plugins[7] Could not load plugin assembly 'Acme.Billing.dll'");
            lines.Add("      System.IO.FileNotFoundException: Could not load file or assembly 'Acme.Billing, Version=1.0.0.0'");
            lines.Add("         at System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath(String assemblyPath)");
            lines.Add("         at PluginHost.Loader.Load(String name) in /src/Loader.cs:line 42");
        }
    }
    var tail = Opt(a, "--tail");
    foreach (var l in tail is not null && int.TryParse(tail, out var n) ? lines.TakeLast(n) : lines) Console.WriteLine(l);
    return Done(0);
}

class Container { public string Id { get; set; } = ""; public string Project { get; set; } = ""; public string Service { get; set; } = ""; public bool Running { get; set; } public string Started { get; set; } = ""; public string? EnvFile { get; set; } public Dictionary<string, string> Env { get; set; } = new(); public string Image { get; set; } = ""; public string Name { get; set; } = ""; public List<string[]> Ports { get; set; } = new(); public List<string[]> Mounts { get; set; } = new(); public Dictionary<string, string> ContainerEnv { get; set; } = new(); }
class State { public List<Container> Containers { get; set; } = new(); public Dictionary<string, Dictionary<string, string>> Images { get; set; } = new(); }
