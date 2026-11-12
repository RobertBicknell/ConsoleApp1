using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// Everything known about a deployment folder, gathered once and used to offer values wherever one is needed:
/// what a scan of the folder found, plus the images and containers docker reports on that host (empty if docker
/// is not reachable; the catalog is still useful without it).
public sealed record Catalog(string Root, DirScan Scan, IReadOnlyList<string> LocalImages, IReadOnlyList<string> Containers, DateTimeOffset At)
{
    /// Every file under the root (same depth and skipped folders as the scan), for browsing.
    public IReadOnlyList<string> AllFiles { get; init; } = [];
    /// The git working trees the root and its stacks live in (usually one).
    public IReadOnlyList<GitFacts> Repos { get; init; } = [];

    /// Services that depend on `service`, directly or through others: what must be restarted with it.
    public IReadOnlyList<string> Dependants(string service)
    {
        var all = Scan.Stacks.SelectMany(s => s.Services).ToList();
        var found = new List<string>();
        var queue = new Queue<string>([service]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var s in all.Where(s => s.DependsOn.Contains(current) && s.Name != service && !found.Contains(s.Name))) { found.Add(s.Name); queue.Enqueue(s.Name); }
        }
        return found;
    }
    private static List<string> D(IEnumerable<string?> xs) =>
        xs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> Services => D(Scan.Stacks.SelectMany(s => s.Services.Select(v => v.Name)));
    public IReadOnlyList<string> ComposeImages => D(Scan.Stacks.SelectMany(s => s.Services.Select(v => v.Image)));
    public IReadOnlyList<string> Images => D(ComposeImages.Concat(LocalImages));
    public IReadOnlyList<string> EnvKeys => D(Scan.Stacks.SelectMany(s => s.Variables.Select(v => v.Name)));
    public IReadOnlyList<string> ComposeFiles => D(Scan.Stacks.SelectMany(s => s.ComposeFiles));
    public IReadOnlyList<string> EnvFiles => D(Scan.Stacks.SelectMany(s => s.Variables.SelectMany(v => v.Refs)).Where(r => r.Kind is "env-file" or "container-env-file").Select(r => r.File).Concat(Scan.UnreferencedEnvFiles));
    public IReadOnlyList<string> AppSettings => D(Scan.Stacks.SelectMany(s => s.AppSettings));
    public IReadOnlyList<string> StackDirs => D(Scan.Stacks.Select(s => s.Dir));
    public IReadOnlyList<string> BindSources => D(Scan.Stacks.SelectMany(s => s.Mounts.Where(m => m.Kind == "bind").Select(m => m.Resolved)));
    public IReadOnlyList<string> Volumes => D(Scan.Stacks.SelectMany(s => s.DeclaredVolumes.Select(v => v.Name).Concat(s.Mounts.Where(m => m.Kind == "volume").Select(m => m.Source!))));
    public IReadOnlyList<string> Projects => D(Scan.Stacks.Select(s => s.Dir[(s.Dir.LastIndexOf('/') + 1)..].ToLowerInvariant()));

    public static async Task<Catalog> BuildAsync(IHost host, string root, int maxDepth, CancellationToken ct)
    {
        var scan = await StackScanner.ScanDirectoryAsync(host, root, maxDepth, ct);
        var images = await Lines(host, ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}"], ct);
        var containers = await Lines(host, ["ps", "-a", "--format", "{{.Names}}"], ct);
        var files = await host.FindFilesAsync(root, ["*"], maxDepth, ct);
        var repos = new List<GitFacts>();
        foreach (var dir in scan.Stacks.Select(s => s.Dir).Prepend(root).Distinct())
            if (await GitRepo.FactsAsync(host, dir, ct) is { } g && repos.All(r => r.Root != g.Root)) repos.Add(g);
        return new Catalog(root, scan, images.Where(i => !i.Contains("<none>")).ToList(), containers, DateTimeOffset.Now) { AllFiles = files, Repos = repos };
    }

    private static async Task<List<string>> Lines(IHost host, string[] dockerArgs, CancellationToken ct)
    {
        try
        {
            var r = await host.ExecAsync("docker", dockerArgs, null, null, ct);
            return r.Code == 0 ? r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList() : new();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(); }     // no docker on this host
    }
}

/// What to offer for an argument or workflow parameter, by its name (and declared type). Heuristic on purpose:
/// the name of an argument says what kind of thing it wants, and the catalog knows the things of that kind.
/// Variables are always offered too, as ${NAME}, so anything can be made indirect.
public static class Suggest
{
    private static bool Is(string name, params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> For(Catalog? c, RunState state, string name, Arg? spec = null, IEnumerable<string>? recent = null)
    {
        var s = new List<string>();
        if (recent is not null) s.AddRange(recent);
        if (spec?.Values is { } values) s.AddRange(values);
        if (spec?.Type == "bool") s.AddRange(["true", "false"]);

        string? V(string key) => state.Vars.TryGetValue(key, out var raw) ? Try(state, raw) : null;
        var pathVars = state.Vars.Where(v => Try(state, v.Value) is { } x && (x.Contains('/') || x.Contains('\\'))).Select(v => $"${{{v.Key}}}").ToList();

        if (Is(name, "service", "consumer") || name is "dependsOn" or "dropDependsOn")
        {
            if (c is not null) s.AddRange(c.Services);
            s.AddRange(state.Vars.Keys.Where(k => k.StartsWith("services.", StringComparison.OrdinalIgnoreCase)).Select(k => k.Split('.')[1]));
        }
        else if (Is(name, "image") || name == "tag") { if (c is not null) s.AddRange(c.Images); }
        else if (name == "target" && spec?.Doc.Contains("Container") == true) { if (c is not null) s.AddRange(c.Containers); }
        else if (Is(name, "envFile")) { s.AddRange(pathVars.Where(v => Is(v, "ENV"))); if (c is not null) s.AddRange(c.EnvFiles); }
        else if (Is(name, "appsettings")) { if (c is not null) s.AddRange(c.AppSettings); }
        else if (name == "files") { s.AddRange(pathVars.Where(v => Is(v, "COMPOSE"))); if (c is not null) s.AddRange(c.ComposeFiles); }
        else if (name is "set" || Is(name, "hostKey", "portKey")) { if (c is not null) s.AddRange(c.EnvKeys); }
        else if (name == "project") { if (c is not null) s.AddRange(c.Projects); }
        else if (name is "contains") s.AddRange(["ERROR", "Exception", "fail:", "warn:", "Unhandled", "Connected to", "Application started"]);
        else if (name is "regex") s.AddRange(["(?i)error|exception|fail", "(?i)warn", @"\b5\d\d\b", "(?i)timeout|refused|unreachable"]);
        else if (Is(name, "dir", "context", "src", "revisionFrom", "projectDir") || name is "path")
        {
            s.AddRange(pathVars);
            if (c is not null) { s.AddRange(c.StackDirs); s.AddRange(c.BindSources); }
        }
        else if (name is "source" or "target" or "a" or "b" or "file" or "paths" or "save" or "volumes")
        {
            s.AddRange(pathVars);
            if (c is not null) { s.AddRange(c.ComposeFiles); s.AddRange(c.EnvFiles); s.AddRange(c.AppSettings); }
        }
        else if (name == "ports" && c is not null) s.AddRange(c.Scan.Stacks.SelectMany(x => x.Ports).Where(p => p.Published is not null).Select(p => $"{p.Published}:{p.Target}"));
        else if (name is "since" or "until") { if (V("STARTED_AT_UTC") is { } t) s.Add(t); s.AddRange(["10m", "1h", "24h"]); }
        else if (name is "tail") s.AddRange(["50", "200", "1000"]);
        else if (name is "waitSeconds") s.AddRange(["10", "30", "60"]);
        else if (name is "strategy" && spec?.Values is null) s.AddRange(["none", "label", "tag"]);

        s.AddRange(state.Vars.Keys.Where(k => !k.Contains('.')).Order(StringComparer.OrdinalIgnoreCase).Select(k => $"${{{k}}}"));
        return s.Where(x => x.Length > 0).Distinct().Take(300).ToList();
    }

    private static string? Try(RunState s, string raw) { try { return s.Expand(raw); } catch (InvalidOperationException) { return null; } }
}
