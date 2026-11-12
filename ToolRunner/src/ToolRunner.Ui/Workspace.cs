using System.Text.Json.Nodes;
using ToolRunner.Core;

namespace ToolRunner.Ui;

/// What the UI knows beyond the config files: the catalog of the deployment folder (scanned at start and on demand),
/// the values tools have produced in this session (they win over configured values, as in a single run), and the
/// values recently typed for each argument (offered first next time).
public sealed class Workspace
{
    private readonly LiveConfig _live;
    private string? _scannedHost = "\0";      // never equal to a real host, so the first Changed triggers nothing extra

    public Catalog? Catalog { get; private set; }
    public bool Scanning { get; private set; }
    public string? ScanError { get; private set; }
    public int Depth { get; set; } = 6;
    public CompareResult? Comparison { get; private set; }
    public bool Comparing { get; private set; }
    public string? CompareError { get; private set; }
    /// The known-good copy the deployment is compared with: REFERENCE_ROOT.
    public string Reference => Var("REFERENCE_ROOT") ?? "";
    public Dictionary<string, string> Produced { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _recent = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;

    // Work in progress that must survive switching tabs (each tab is created afresh when shown).
    /// The chain being assembled on the Run tab. Saving it makes it a workflow; until then it lives only here.
    public List<StepDef> Chain { get; } = new();
    public string? ChainName { get; set; }
    public string? ChainDescription { get; set; }
    /// Values typed for parameters and ${names}, shared by everything on the Run tab.
    public Dictionary<string, string> RunVars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LogPane> LogPanes { get; } = new();

    /// The safety switch. On (green): only reading is allowed; any step that would change the deployment is refused.
    /// Off (red): everything runs. The tool's own data (history, variables in your layer, saved workflows) is never blocked.
    public bool ReadOnly { get; private set; } = true;
    public void SetReadOnly(bool on) { ReadOnly = on; Changed?.Invoke(); }

    /// Variables the user wants to keep an eye on while running things.
    public List<string> Watch { get; } = new();
    /// Where the Run tab shows output: "below" the command (variables beside it) or "beside" it.
    public string RunLayout { get; set; } = "below";

    public RuntimeReport? Runtime { get; private set; }
    public bool CheckingRuntime { get; private set; }

    /// Tallies the scanned configuration against `docker inspect` on the selected host. Read-only.
    public async Task CheckRuntimeAsync()
    {
        if (CheckingRuntime || Catalog is null) return;
        CheckingRuntime = true; Changed?.Invoke();
        try { Runtime = await Task.Run(() => RuntimeCheck.RunAsync(_live.CreateHost(), Catalog.Scan, CancellationToken.None)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { Runtime = new RuntimeReport([], [], [ex.Message]); }
        finally { CheckingRuntime = false; Changed?.Invoke(); }
    }

// ---- versions
    /// What the image tags of the scanned deployment say; null until a scan has finished.
    public StackVersions? Versions { get; private set; }
    /// Every stack has its own version; Versions is the stack APP_ROOT points at (else the first).
    public IReadOnlyList<(string Stack, StackVersions Versions)> VersionsByStack { get; private set; } = [];
    public VersionScheme Scheme => VersionScheme.From(Var);
    /// VERSION when the user has pinned one; builds and checkouts use it instead of the detected version.
    public string? PinnedVersion => Var("VERSION") is { Length: > 0 } v ? v : null;
    public string? EffectiveVersion => PinnedVersion ?? Versions?.Detected;
    public AvailableVersions? Available { get; private set; }
    public bool ListingVersions { get; private set; }

    public void SetVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) _live.ResetVar("VERSION"); else _live.SetVar("VERSION", version.Trim());
        _live.Reload();
        Changed?.Invoke();
    }

    private async Task<List<(string Stack, StackVersions Versions)>> ReadVersionsAsync(Catalog cat)
    {
        var host = _live.CreateHost();
        var all = new List<(string, StackVersions)>();
        foreach (var stack in cat.Scan.Stacks)
        {
            var texts = new Dictionary<string, string>();
            foreach (var f in stack.Services.Select(s => s.File).Distinct())
                if (await host.ReadTextAsync(f, CancellationToken.None) is { } t) texts[f] = t;
            all.Add((stack.Dir, Core.Versions.Summarise(Core.Versions.Read(stack.Dir, stack.Services, texts, Scheme))));
        }
        return all;
    }

    /// What exists: the git refs of a source repo that are versions, and the registry tags of an image. Read-only.
    public async Task ListVersionsAsync(string? repo, string? image)
    {
        if (ListingVersions) return;
        ListingVersions = true; Changed?.Invoke();
        var notes = new List<string>();
        try
        {
            var (host, scheme) = (_live.CreateHost(), Scheme);
            List<GitVersion>? git = null; string? head = null; (string Ref, string Commit)? at = null; List<string>? registry = null;
            if (!string.IsNullOrWhiteSpace(repo))
            {
                head = await Core.Versions.HeadAsync(host, repo, CancellationToken.None);
                if (head is null) notes.Add($"{repo} is not a git working tree");
                else
                {
                    git = await Core.Versions.FromGitAsync(host, repo, scheme, CancellationToken.None);
                    if (EffectiveVersion is { } v) at = await Core.Versions.ResolveAsync(host, repo, v, scheme, CancellationToken.None);
                }
            }
            if (!string.IsNullOrWhiteSpace(image))
            {
                if (Var("REGISTRY_API") is not { Length: > 0 } api) notes.Add("REGISTRY_API is not set, so the registry was not asked (variables tab; see the README)");
                else
                {
                    var (tags, note) = await RegistryClient.TagsAsync(api, Core.Versions.RegistryRepo(image, Var("REGISTRY_PREFIX")), Var("REGISTRY_USER"), Var("REGISTRY_TOKEN"), CancellationToken.None);
                    if (tags is null) notes.Add(note!); else registry = Core.Versions.FromTags(tags, scheme);
                }
            }
            Available = new AvailableVersions(repo, git, head, at, image, registry, notes);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { Available = new AvailableVersions(repo, null, null, null, image, null, [ex.Message]); }
        finally { ListingVersions = false; Changed?.Invoke(); }
    }

    // ---- pipelines (Azure DevOps + JFrog)
    public Exploration? Exploration { get; private set; }
    public bool Exploring { get; private set; }
    public List<string> ExploreLog { get; } = new();
    public PipelineSettings Pipeline => PipelineSettings.From(Var);

    public void LoadExploration() => Exploration ??= Pipelines.Load(_live.Dir);

    public void SetSetting(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) _live.ResetVar(key); else _live.SetVar(key, value.Trim());
        _live.Reload();
        Changed?.Invoke();
    }

    /// Reads Azure DevOps and Artifactory (GETs only) and keeps the result on disk for the next session.
    public async Task ExploreAsync()
    {
        if (Exploring) return;
        Exploring = true; lock (ExploreLog) ExploreLog.Clear(); Changed?.Invoke();
        try
        {
            var settings = Pipeline;
            Exploration = await Task.Run(() => Pipelines.ExploreAsync(settings, CancellationToken.None, line => { lock (ExploreLog) ExploreLog.Add(line); Changed?.Invoke(); }));
            Pipelines.Save(_live.Dir, Exploration);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UriFormatException) { lock (ExploreLog) ExploreLog.Add("failed: " + ex.Message); }
        finally { Exploring = false; Changed?.Invoke(); }
    }

    // ---- building a new deployment
    public BuildSettings Build => BuildSettings.From(Var);
    public BuildPlan BuildPlan() => DeploymentBuilder.Plan(Build, _live.Dir, EffectiveVersion);
    public List<BuildStep> BuildSteps { get; } = new();
    public string? BuildTitle { get; private set; }
    public bool Building { get; private set; }

    /// write | fetch | check | test: each produces a list of steps shown on the build tab.
    public async Task BuildAsync(string action)
    {
        if (Building) return;
        Building = true; BuildTitle = action; lock (BuildSteps) BuildSteps.Clear(); Changed?.Invoke();
        void Add(BuildStep s) { lock (BuildSteps) BuildSteps.Add(s); Changed?.Invoke(); }
        try
        {
            var (plan, settings, host) = (BuildPlan(), Build, _live.CreateHost());
            await Task.Run(async () =>
            {
                switch (action)
                {
                    case "write":
                        var written = await DeploymentBuilder.WriteAsync(plan, host, CancellationToken.None);
                        Add(new BuildStep("files written", true, $"{written.Count} file(s) in {plan.OutputDir}"));
                        foreach (var s in await DeploymentBuilder.CheckAsync(plan.OutputDir, host, CancellationToken.None)) Add(s);
                        break;
                    case "fetch":
                        if (Var("JFROG_URL") is not { Length: > 0 } jfrog || settings["BUILD_NUGET_REPO"].Length == 0) { Add(new BuildStep("plugin packages", false, "set JFROG_URL (pipelines tab) and the plugin package repository first")); break; }
                        foreach (var s in await DeploymentBuilder.FetchPluginsAsync(plan, jfrog, settings["BUILD_NUGET_REPO"], Var("JFROG_USER"), Var("JFROG_TOKEN"), CancellationToken.None)) Add(s);
                        foreach (var s in await DeploymentBuilder.CheckAsync(plan.OutputDir, host, CancellationToken.None)) Add(s);
                        break;
                    case "check": foreach (var s in await DeploymentBuilder.CheckAsync(plan.OutputDir, host, CancellationToken.None)) Add(s); break;
                    case "test": await DeploymentBuilder.TestAsync(plan, settings, host, CancellationToken.None, Add); break;
                }
            });
            History.Remark(_live.Dir, $"build {action} for {plan.Value("PRODUCT_TITLE")} {plan.Value("VERSION")} in {plan.OutputDir}: " + string.Join("; ", BuildSteps.Select(s => $"{(s.Ok ? "ok" : "FAIL")} {s.Name}")));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { Add(new BuildStep(action, false, ex.Message)); }
        finally { Building = false; Changed?.Invoke(); }
    }

    public string Engine => Var("CONTAINER_ENGINE") is { Length: > 0 } e ? e : "docker";
    public void ToggleEngine() { SetSetting("CONTAINER_ENGINE", Engine == "docker" ? "podman" : null); _ = RescanAsync(); }

    public void Remark(string text) { History.Remark(_live.Dir, text); Changed?.Invoke(); }

    /// The stack a service belongs to, and the compose arguments that address that stack whatever APP_ROOT says.
    public Stack? StackOf(string service) => Catalog?.Scan.Stacks.FirstOrDefault(s => s.Services.Any(v => v.Name == service));

    public JsonObject ComposeArgs(string verb, string service, IEnumerable<string>? alsoServices = null)
    {
        var args = new JsonObject { ["verb"] = verb };
        if (StackOf(service) is { } stack)
        {
            args["projectDir"] = stack.Dir;
            args["files"] = new JsonArray(stack.ComposeFiles.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            if (stack.Variables.SelectMany(v => v.Refs).FirstOrDefault(r => r.Kind == "env-file") is { } env) args["envFile"] = env.File;
        }
        args["services"] = new JsonArray(new[] { service }.Concat(alsoServices ?? []).Distinct().Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        return args;
    }
    public string LogFilter { get; set; } = "";
    public HashSet<string> LogLevels { get; } = new(LogLens.Levels);
    public bool LogDigest { get; set; }

    public Workspace(LiveConfig live)
    {
        _live = live;
        _scannedHost = live.Host;
        live.Changed += () => { if (live.Host != _scannedHost) { _scannedHost = live.Host; _ = RescanAsync(); } };   // another host = another deployment
        _ = RescanAsync();
    }

    /// The deployment folder: DEPLOY_ROOT if set, else APP_ROOT.
    public string Root => Var("DEPLOY_ROOT") ?? Var("APP_ROOT") ?? "";

    private string? Var(string key)
    {
        if (!_live.State.Vars.TryGetValue(key, out var raw)) return null;
        try { return _live.State.Expand(raw); } catch (InvalidOperationException) { return null; }
    }

    /// Configured variables with this session's tool outputs layered on top.
    public RunState State()
    {
        var s = new RunState();
        foreach (var (k, v) in _live.State.Vars) s.Vars[k] = v;
        lock (Produced) foreach (var (k, v) in Produced) s.Vars[k] = v;
        return s;
    }

    public void Absorb(IReadOnlyDictionary<string, string> produced)
    {
        lock (Produced) foreach (var (k, v) in produced) Produced[k] = v;
        Changed?.Invoke();
    }

    public void Forget(string? key = null)
    {
        lock (Produced) { if (key is null) Produced.Clear(); else Produced.Remove(key); }
        Changed?.Invoke();
    }

    public void SetRoot(string root)
    {
        ConfigWriter.SetUserVar(_live.Dir, "DEPLOY_ROOT", root.Trim().Replace('\\', '/'));
        _live.Reload();
        _ = RescanAsync();
    }

    public async Task RescanAsync()
    {
        if (Scanning || Root.Length == 0) return;
        (Scanning, ScanError) = (true, null);
        Changed?.Invoke();
        try
        {
            Catalog = await Task.Run(() => Catalog.BuildAsync(_live.CreateHost(), Root, Depth, CancellationToken.None));
            VersionsByStack = await Task.Run(() => ReadVersionsAsync(Catalog));
            var app = ComposeLayout.Collapse(Var("APP_ROOT") ?? "");
            Versions = VersionsByStack.Where(s => ComposeLayout.Collapse(s.Stack).Equals(app, StringComparison.OrdinalIgnoreCase)).Select(s => s.Versions)
                .Concat(VersionsByStack.Select(s => s.Versions)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { ScanError = ex.Message; }
        finally { Scanning = false; Changed?.Invoke(); }
    }

    /// Compares the deployment folder with a reference folder on the selected host and remembers the reference.
    public async Task CompareAsync(string current, string reference)
    {
        if (Comparing) return;
        (Comparing, CompareError) = (true, null);
        Changed?.Invoke();
        try
        {
            reference = reference.Trim().Replace('\\', '/');
            if (reference != Reference) { ConfigWriter.SetUserVar(_live.Dir, "REFERENCE_ROOT", reference); _live.Reload(); }
            Comparison = await Task.Run(async () =>
            {
                // "git:REF" = this folder as it was at that commit (branch, tag, HEAD ...)
                var host = _live.CreateHost();
                var fromGit = reference.StartsWith("git:");
                var folder = fromGit ? await GitRepo.MaterialiseAsync(host, current.Trim(), reference[4..], Path.Combine(History.Dir(_live.Dir), "refs"), CancellationToken.None) : reference;
                return await DeploymentCompare.RunAsync(host, current.Trim(), folder, 8, CancellationToken.None, ignoreLineEndings: fromGit);
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { CompareError = ex.Message; }
        finally { Comparing = false; Changed?.Invoke(); }
    }

    public IReadOnlyList<string> Suggest(string name, Arg? spec = null) =>
        Core.Suggest.For(Catalog, State(), name, spec, _recent.GetValueOrDefault(name));

    public void Remember(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!_recent.TryGetValue(name, out var list)) _recent[name] = list = new();
        list.Remove(value); list.Insert(0, value);
        if (list.Count > 8) list.RemoveAt(8);
    }

    /// A step's full arguments: its named set from args.json with the step's own overrides on top. Null if the set is unknown.
    public JsonObject? Merged(StepDef step)
    {
        var args = new JsonObject();
        var parts = step.Run.Split('/', 2);
        if (parts.Length == 2)
        {
            if (!_live.Cfg.Args.TryGetValue(parts[0], out var sets) || !sets.TryGetValue(parts[1], out var set)) return null;
            foreach (var (k, v) in set) args[k] = v?.DeepClone();
        }
        if (step.With is not null) foreach (var (k, v) in step.With) args[k] = v?.DeepClone();
        return args;
    }

    /// The ${names} these steps need that nothing defines yet: what the user must supply (and a saved workflow's parameters).
    public List<string> Needs(IEnumerable<StepDef> steps) =>
        ConfigWriter.ImpliedParams(steps.Select(Merged).OfType<JsonObject>(), _live.State, WellKnownOutputs.Concat(Produced.Keys));

    // Variables that tools write during a run, so a chain that reads them is not asked for them.
    private static readonly string[] WellKnownOutputs =
        { "STARTED_AT_UTC", "GIT_HEAD", "GIT_DIRTY_COUNT", "DIFF_LINES", "SOURCE_COMMIT", "IMAGE", "LOG_LINES", "LOG_MATCHES", "LOG_FIRST_MATCH",
          "DOCKER_PS", "DOCKER_INSPECT", "DOCKER_HISTORY", "DOCKER_RUN", "PLUGINS_MISSING", "PLUGINS_LISTED",
          "DEBUG_SERVICE", "DEBUG_IMAGE", "DEBUG_BASE_IMAGE", "DEBUG_DOCKERFILE", "DEBUG_CONTEXT", "DEBUG_SOURCE_ROOT", "DEBUG_VSDBG_MOUNT", "DEBUG_VSDBG_PATH",
          "VERSION_DETECTED", "SOURCE_REF" };
}

/// The versions found for one source repo and one image. At = where the version in use sits in the repo (null: no such ref).
public sealed record AvailableVersions(string? Repo, List<GitVersion>? Git, string? Head, (string Ref, string Commit)? At, string? Image, List<string>? Registry, List<string> Notes);

/// One service's logs on the Logs tab.
public sealed class LogPane(string service)
{
    public string Service { get; } = service;
    public string Tail { get; set; } = "1000";
    public string Since { get; set; } = "";
    public List<(string Level, string Text)> Lines { get; set; } = new();
    public bool Busy { get; set; }
    public string? Error { get; set; }
    public string? SavedTo { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
}
