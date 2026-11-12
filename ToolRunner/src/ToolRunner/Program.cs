using System.Text.Json;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

// stdout: machine-readable results only (JSON, listings). stderr: everything a human reads.
// So `toolrunner debug pluginhost > out.json` and `| jq` behave.

string? configArg = null, hostName = null;
var dryRun = false; var readOnly = false;
var sets = new List<string>();
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config":  configArg = args[++i]; break;
        case "--host":    hostName = args[++i]; break;
        case "--set":     sets.Add(args[++i]); break;
        case "--dry-run": dryRun = true; break;
        case "--read-only": readOnly = true; break;
        default:          rest.Add(args[i]); break;
    }
}

var transcript = new System.Text.StringBuilder();    // the human log, kept for the history journal
var startedAt = DateTimeOffset.UtcNow;
void Log(string s) { Console.Error.WriteLine(s); lock (transcript) transcript.AppendLine(s); }
if (rest.Count == 0 || rest[0] is "help" or "--help" or "-h") { Usage(); return 2; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var registry = Registry.Default();

try
{
    var dir = ConfigLoader.ResolveDir(configArg);
    var state = ConfigLoader.LoadState(dir, hostName, sets);
    var cfg = ConfigLoader.LoadConfig(dir);

    switch (rest[0])
    {
        case "tools":
            foreach (var t in registry.All) Console.WriteLine($"{t.Name,-8} verbs: {string.Join(", ", t.Verbs)}");
            return 0;

        case "workflows":
            foreach (var (n, w) in cfg.Workflows)
                Console.WriteLine($"{n} {string.Join(' ', w.Params.Select(p => $"<{p}>"))}  {w.Description}".TrimEnd());
            return 0;

        case "args":
            foreach (var (tool, intents) in cfg.Args)
                foreach (var (intent, set) in intents)
                    Console.WriteLine($"{tool}/{intent}  verb={ToolContext.NodeStr(set["verb"])}");
            return 0;

        case "spool":    // one plain-text file of context for a chat assistant: spool [out=F] [last=N] [note=..] [withConfig=true] [defang=true] [file ...]
        {
            var kv = rest.Skip(1).Where(t => t.IndexOf('=') > 0).ToDictionary(t => t[..t.IndexOf('=')], t => t[(t.IndexOf('=') + 1)..], StringComparer.OrdinalIgnoreCase);
            var files = rest.Skip(1).Where(t => t.IndexOf('=') <= 0).Concat((kv.GetValueOrDefault("files") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)).ToList();
            var o = new SpoolOptions(int.Parse(kv.GetValueOrDefault("last", "5")), files, bool.Parse(kv.GetValueOrDefault("withConfig", "false")),
                                     bool.Parse(kv.GetValueOrDefault("defang", "false")), kv.GetValueOrDefault("note") ?? kv.GetValueOrDefault("intent"),
                                     kv.GetValueOrDefault("mode") ?? (kv.ContainsKey("intent") ? "plan" : "ask"));
            var scan = bool.Parse(kv.GetValueOrDefault("scan", "false"))      // env vars found in the compose/env files, read on the selected host
                ? await Scanner.RunAsync(state, ConfigLoader.CreateHost(state, hostName), cts.Token) : default;
            var dirScan = kv.TryGetValue("scanDir", out var sd)             // recursive scan of a directory on the selected host
                ? await StackScanner.ScanDirectoryAsync(ConfigLoader.CreateHost(state, hostName), state.Expand(sd), int.Parse(kv.GetValueOrDefault("depth", "6")), cts.Token) : null;
            var compare = kv.TryGetValue("compareRef", out var cref)            // differences from a reference folder
                ? await DeploymentCompare.RunAsync(ConfigLoader.CreateHost(state, hostName),
                      state.Expand(kv.GetValueOrDefault("scanDir") ?? (state.Vars.ContainsKey("DEPLOY_ROOT") ? "${DEPLOY_ROOT}" : "${APP_ROOT}")), state.Expand(cref), 8, cts.Token) : null;
            var text = Spool.Build(dir, state, History.Last(dir, o.Last), o, scan.Rows, dirScan, compare,
                o.Mode == "plan" ? [Planning.OptionsSection(registry, cfg, state)] : null);
            var outPath = kv.GetValueOrDefault("out") ?? Path.Combine(History.Dir(dir), "spool.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, text, new System.Text.UTF8Encoding(false));
            Log($"wrote {outPath} ({text.Length:N0} chars)");
            Console.WriteLine(Path.GetFullPath(outPath));
            return 0;
        }

        case "build":    // build plan | write | fetch | check | test   (a new deployment folder from product.json, a title, a version and templates)
        {
            string? V(string k) => state.Vars.TryGetValue(k, out var raw) ? state.Expand(raw) : null;
            var settings = BuildSettings.From(V);
            var plan = DeploymentBuilder.Plan(settings, dir, V("VERSION"));
            var sub = rest.Count > 1 ? rest[1] : "plan";
            var target = ConfigLoader.CreateHost(state, hostName);
            int Report(IEnumerable<BuildStep> steps) { var all = steps.ToList(); foreach (var st in all) Console.WriteLine($"{(st.Ok ? "ok  " : "FAIL")} {st.Name}: {st.Detail}"); return all.All(st => st.Ok) ? 0 : 1; }
            foreach (var n in plan.Notes) Log($"note: {n}");
            foreach (var p in plan.Problems) Log($"PROBLEM: {p}");
            switch (sub)
            {
                case "plan":
                    Console.WriteLine($"entities ({plan.Entities.Count}): {string.Join(", ", plan.Entities)}");
                    foreach (var (k, v) in plan.Values.Where(v => !v.Key.StartsWith("__"))) Console.WriteLine($"  {k} = {(Spool.IsSecret(k) ? "(set)" : v)}");
                    foreach (var f in plan.Files.Keys) Console.WriteLine($"file {plan.OutputDir}/{f}");
                    return plan.Problems.Count == 0 ? 0 : 1;
                case "write":
                    if (readOnly) { Log("BLOCKED: build write creates files, and read-only mode is on"); return 1; }
                    if (plan.Problems.Count > 0) return 1;
                    foreach (var f in await DeploymentBuilder.WriteAsync(plan, target, cts.Token)) Console.WriteLine($"{plan.OutputDir}/{f}");
                    return Report(await DeploymentBuilder.CheckAsync(plan.OutputDir, target, cts.Token));
                case "fetch":
                    if (readOnly) { Log("BLOCKED: build fetch writes plugin files, and read-only mode is on"); return 1; }
                    if (V("JFROG_URL") is not { Length: > 0 } jfrog || settings["BUILD_NUGET_REPO"].Length == 0) { Log("set JFROG_URL and BUILD_NUGET_REPO first"); return 2; }
                    return Report(await DeploymentBuilder.FetchPluginsAsync(plan, jfrog, settings["BUILD_NUGET_REPO"], V("JFROG_USER"), V("JFROG_TOKEN"), cts.Token));
                case "check": return Report(await DeploymentBuilder.CheckAsync(plan.OutputDir, target, cts.Token));
                case "test":
                    if (readOnly) { Log("BLOCKED: build test starts the stack, and read-only mode is on"); return 1; }
                    if (plan.Problems.Count > 0) return 1;
                    return Report(await DeploymentBuilder.TestAsync(plan, settings, target, cts.Token, st => Log($"{(st.Ok ? "ok  " : "FAIL")} {st.Name}")));
                default: throw new ArgumentException("usage: build plan | write | fetch | check | test");
            }
        }

        case "azdo":     // azdo explore | assess | yaml [out=DIR] | apply confirm=true   (Azure DevOps + JFrog: what exists, and the two pipelines)
        {
            string? V(string k) => state.Vars.TryGetValue(k, out var raw) ? state.Expand(raw) : null;
            var settings = PipelineSettings.From(V);
            var kv = rest.Skip(2).Where(t => t.IndexOf('=') > 0).ToDictionary(t => t[..t.IndexOf('=')], t => t[(t.IndexOf('=') + 1)..], StringComparer.OrdinalIgnoreCase);
            var sub = rest.Count > 1 ? rest[1] : "assess";
            var found = sub == "explore" ? await Pipelines.ExploreAsync(settings, cts.Token, Log) : Pipelines.Load(dir);
            if (found is null) { Log("nothing explored yet: run `toolrunner azdo explore`"); return 2; }
            if (sub == "explore") { Pipelines.Save(dir, found); Log($"saved {Pipelines.StorePath(dir)}"); }
            switch (sub)
            {
                case "explore": case "assess":
                    foreach (var n in found.Azdo.Notes.Concat(found.Jfrog.Notes)) Log($"note: {n}");
                    foreach (var (k, v) in Pipelines.Suggest(found, settings)) Log($"suggest: {k} = {v}");
                    var checks = Pipelines.Assess(found, settings);
                    foreach (var c in checks) Console.WriteLine($"{c.Status.ToUpperInvariant(),-8} {c.Pipeline,-8} {c.Name}: {c.Evidence}{(c.Fix.Length > 0 ? "  -> " + c.Fix : "")}");
                    Console.WriteLine($"tags: {Pipelines.TagConvention(found)}");
                    foreach (var row in Pipelines.VersionTable(found, settings, VersionScheme.From(V)).Take(15))
                        Console.WriteLine($"version {row.Version,-14} plugins-tag:{(row.PluginsTag ? "y" : "-")} config-tag:{(row.ConfigTag ? "y" : "-")} package:{(row.Package ? "y" : "-")} image:{(row.Image ? "y" : "-")}");
                    return checks.Any(c => c.Status == "missing") ? 1 : 0;
                case "yaml":
                {
                    var root = V("DEPLOY_ROOT") ?? V("APP_ROOT");
                    var scan = root is null ? null : await StackScanner.ScanDirectoryAsync(ConfigLoader.CreateHost(state, hostName), root, 6, cts.Token);
                    var bake = settings.List("PIPELINE_BAKE_SERVICES").ToList();
                    var stack = scan?.Stacks.OrderByDescending(st => st.Services.Count(sv => bake.Contains(sv.Name))).FirstOrDefault();
                    var outDir = kv.GetValueOrDefault("out") ?? Path.Combine(History.Dir(dir), "pipelines");
                    foreach (var (repo, path, content) in Pipelines.Files(settings, stack))
                    {
                        var file = Path.Combine(outDir, repo.Length > 0 ? repo : "_repository-not-set", path);
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, content, new System.Text.UTF8Encoding(false));
                        Console.WriteLine(Path.GetFullPath(file));
                    }
                    return 0;
                }
                case "apply":   // registers the two YAML pipelines; the YAML must already be committed to the repositories
                {
                    if (kv.GetValueOrDefault("confirm") != "true") { Log("this creates pipelines in Azure DevOps; add confirm=true"); return 2; }
                    var failed = false;
                    foreach (var (key, name) in new[] { ("PIPELINE_PLUGINS_REPO", settings["PIPELINE_PRODUCT"] + "-plugins"), ("PIPELINE_CONFIG_REPO", settings["PIPELINE_PRODUCT"] + "-product") })
                    {
                        var repo = found.Azdo.Repos.FirstOrDefault(x => x.Name.Equals(settings[key], StringComparison.OrdinalIgnoreCase));
                        if (repo is null) { Log($"{key}: repository '{settings[key]}' was not found by the last explore"); failed = true; continue; }
                        var (created, message) = await AzdoExplorer.CreatePipelineAsync(settings["AZDO_URL"], found.Azdo.Project!, settings["AZDO_PAT"], found.Azdo.ApiVersion!, name, repo.Id, repo.Name, "/azure-pipelines.yml", cts.Token);
                        Log(message); failed |= !created;
                    }
                    return failed ? 1 : 0;
                }
                default: throw new ArgumentException("usage: azdo explore | assess | yaml [out=DIR] | apply confirm=true");
            }
        }

        case "remark":   // a note in the history stream: remark <text...>
            if (rest.Count < 2) throw new ArgumentException("usage: remark <text>");
            History.Remark(dir, string.Join(' ', rest.Skip(1)));
            Log("noted");
            return 0;

        case "docs":     // docs [out=FILE]: the HTML documentation, with its reference tables read from this build and this config
        {
            var template = Path.Combine(dir, "docs.template.html");
            if (!File.Exists(template)) { Log($"no docs.template.html in {dir}"); return 2; }
            var outFile = rest.Skip(1).FirstOrDefault(t => t.StartsWith("out="))?[4..];
            if (outFile is null)
            {
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "ToolRunner.sln"))) root = root.Parent;
                outFile = Path.Combine(root?.FullName ?? Directory.GetCurrentDirectory(), "docs", "index.html");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, Docs.Generate(File.ReadAllText(template), registry, cfg), new System.Text.UTF8Encoding(false));
            Console.WriteLine(Path.GetFullPath(outFile));
            return 0;
        }

        case "schema":   // (re)generate config/schemas/*.schema.json for editor IntelliSense
        {
            var written = Schemas.Write(dir, registry, cfg);
            Log(written.Count == 0 ? "schemas are up to date" : $"wrote {string.Join(", ", written)} in {Path.Combine(dir, "schemas")}");
            return 0;
        }

        case "vars":     // the resolved variable hierarchy, to debug globals layering
            foreach (var (k, v) in state.Vars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                string shown;
                try { shown = state.Expand(v); } catch (InvalidOperationException ex) { shown = $"<{ex.Message}>"; }
                Console.WriteLine($"{k} = {shown}");
            }
            return 0;
    }

    var host = ConfigLoader.CreateHost(state, hostName);
    Log($"config: {dir}   host: {host.Name}");

    if (rest[0] == "doctor") return await Doctor.RunAsync(host, Log, cts.Token) ? 0 : 1;

    if (rest[0] == "runtime")
    {
        // runtime [dir=PATH] [format=json|text]: is the configuration in effect? Tallies a scan against `docker inspect`.
        var kv = rest.Skip(1).Where(t => t.IndexOf('=') > 0).ToDictionary(t => t[..t.IndexOf('=')], t => t[(t.IndexOf('=') + 1)..], StringComparer.OrdinalIgnoreCase);
        var root = state.Expand(kv.GetValueOrDefault("dir") ?? (state.Vars.ContainsKey("DEPLOY_ROOT") ? "${DEPLOY_ROOT}" : "${APP_ROOT}"));
        var scan = await StackScanner.ScanDirectoryAsync(host, root, int.Parse(kv.GetValueOrDefault("depth", "6")), cts.Token);
        var report = await RuntimeCheck.RunAsync(host, scan, cts.Token);
        Console.WriteLine(kv.GetValueOrDefault("format", "json") == "text" ? RuntimeCheck.Text(report, root) : JsonSerializer.Serialize(report, ConfigLoader.JsonOptions));
        return report.Notes.Count > 0 ? 2 : report.Problems == 0 ? 0 : 1;
    }

    if (rest[0] == "compare")
    {
        // compare [dir=PATH] [ref=PATH] [depth=8] [format=json|text]: how a deployment folder differs from a reference folder
        var kv = rest.Skip(1).Where(t => t.IndexOf('=') > 0).ToDictionary(t => t[..t.IndexOf('=')], t => t[(t.IndexOf('=') + 1)..], StringComparer.OrdinalIgnoreCase);
        string Need(string arg, params string[] vars) =>
            kv.TryGetValue(arg, out var given) ? state.Expand(given)
            : vars.Where(state.Vars.ContainsKey).Select(v => state.Expand(state.Vars[v])).FirstOrDefault()
              ?? throw new ArgumentException($"compare needs {arg}=PATH (or set {string.Join(" / ", vars)})");
        var (here, reference) = (Need("dir", "DEPLOY_ROOT", "APP_ROOT"), Need("ref", "REFERENCE_ROOT"));
        var fromGit = reference.StartsWith("git:");      // ref=git:HEAD (or a branch, tag, commit): the folder as it was at that commit
        if (fromGit) reference = await GitRepo.MaterialiseAsync(host, here, reference[4..], Path.Combine(History.Dir(dir), "refs"), cts.Token);
        var result = await DeploymentCompare.RunAsync(host, here, reference, int.Parse(kv.GetValueOrDefault("depth", "8")), cts.Token, ignoreLineEndings: fromGit);
        Console.WriteLine(kv.GetValueOrDefault("format", "json") == "text" ? DeploymentCompare.Text(result) : JsonSerializer.Serialize(result, ConfigLoader.JsonOptions));
        return result.Notes.Count > 0 ? 2 : result.Diffs.Count == 0 ? 0 : 1;       // like diff: 0 same, 1 different, 2 trouble
    }

    if (rest[0] == "scan" && rest.Skip(1).Any(t => t.StartsWith("dir=")))
    {
        // scan dir=PATH [depth=6] [format=json|text]: recurse a directory on the host; variables, ports, volumes, bind mounts per stack
        var kv = rest.Skip(1).Where(t => t.IndexOf('=') > 0).ToDictionary(t => t[..t.IndexOf('=')], t => t[(t.IndexOf('=') + 1)..], StringComparer.OrdinalIgnoreCase);
        var result = await StackScanner.ScanDirectoryAsync(host, state.Expand(kv["dir"]), int.Parse(kv.GetValueOrDefault("depth", "6")), cts.Token);
        Console.WriteLine(kv.GetValueOrDefault("format", "json") == "text" ? StackScanner.Text(result) : JsonSerializer.Serialize(result, ConfigLoader.JsonOptions));
        return 0;
    }

    if (rest[0] == "scan")      // every environment variable the compose/env files mention, as JSON on stdout
    {
        var (rows, notes) = await Scanner.RunAsync(state, host, cts.Token);
        foreach (var n in notes) Log($"note: {n}");
        Console.WriteLine(JsonSerializer.Serialize(rows.Select(r => new { r.Name, r.Category, r.Status, r.Value, Sources = r.Refs.Select(x => $"{x.Kind}{(x.Service is null ? "" : " [" + x.Service + "]")} {x.File}:{x.Line}") }), ConfigLoader.JsonOptions));
        return 0;
    }

    IReadOnlyList<StepDef> steps;
    if (rest[0] == "run")      // one named arg set: run compose/up-debug [key=value ...]
    {
        if (rest.Count < 2) throw new ArgumentException("usage: run <tool>/<intent> [key=value ...]");
        var with = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in rest.Skip(2))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"expected key=value, got '{kv}'");
            var val = kv[(eq + 1)..];
            // [..] and {..} that parse as JSON are structured (lists, edit objects); anything else, including
            // Go templates like {{.Names}}, stays a plain string.
            System.Text.Json.Nodes.JsonNode? parsed = null;
            if (val.TrimStart().StartsWith('[') || val.TrimStart().StartsWith('{'))
                try { parsed = System.Text.Json.Nodes.JsonNode.Parse(val); } catch (JsonException) { }
            with[kv[..eq]] = parsed ?? val;
        }
        steps = new[] { new StepDef { Run = rest[1], With = with } };
    }
    else if (rest[0] == "plan")     // plan <reply-file> [run=true]: read an assistant's PLAN, check it, and (with run=true) execute it
    {
        if (rest.Count < 2) throw new ArgumentException("usage: plan <file with the assistant's reply> [run=true]");
        var plan = Planning.Parse(File.ReadAllText(rest[1]), registry, cfg, state);
        foreach (var line in Planning.Describe(plan)) Log(line);
        if (plan.Problems.Count > 0) return 2;
        if (!rest.Skip(2).Contains("run=true", StringComparer.OrdinalIgnoreCase)) { Log("checked, not run (add run=true)"); return 0; }
        foreach (var (k, v) in plan.Vars) state.Vars[k] = v;
        steps = plan.Steps;
    }
    else if (cfg.Workflows.TryGetValue(rest[0], out var wf))
    {
        Bind(state, wf, rest.Skip(1));
        steps = wf.Steps;
    }
    else { Log($"Unknown command or workflow '{rest[0]}'"); Usage(); return 2; }

    var runner = new WorkflowRunner(registry, cfg, Log);
    var ok = await runner.RunAsync(steps, state, host, dryRun, cts.Token, readOnly);
    Console.WriteLine(JsonSerializer.Serialize(state.Produced, ConfigLoader.JsonOptions));
    if (!dryRun)
        History.Append(dir, new RunRecord(startedAt, host.Name, args, ok ? 0 : 1, transcript.ToString(), new(state.Produced)));
    return ok ? 0 : 1;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
{
    Log($"error: {ex.Message}");
    return 2;
}

// `debug pluginhost`: params bind positionally in declaration order; `name=value` binds by name.
static void Bind(RunState state, WorkflowDef wf, IEnumerable<string> tokens)
{
    var bound = new Dictionary<string, string>(wf.Defaults, StringComparer.OrdinalIgnoreCase);
    var pos = 0;
    foreach (var t in tokens)
    {
        var eq = t.IndexOf('=');
        if (eq > 0) bound[t[..eq]] = t[(eq + 1)..];
        else if (pos < wf.Params.Count) bound[wf.Params[pos++]] = t;
        else throw new ArgumentException($"Unexpected argument '{t}'");
    }
    var missing = wf.Params.Where(p => !bound.ContainsKey(p)).ToList();
    if (missing.Count > 0) throw new ArgumentException($"Missing parameter(s): {string.Join(", ", missing)}");
    foreach (var (k, v) in bound) state.Vars[k] = v;
}

static void Usage() => Console.Error.WriteLine("""
    usage: toolrunner [--config DIR] [--host NAME] [--set KEY=VALUE]... [--dry-run] <command>

      <workflow> [params...]          run a workflow from workflows.json        e.g.  debug pluginhost
      run <tool>/<intent> [k=v ...]   run one named arg set from args.json      e.g.  run compose/up-service service=gateway
      doctor                          check git/docker are reachable on the host
      schema                          write JSON Schemas for the config files (editor completion and validation)
      scan                            list every environment variable the compose/env files use, with status (stdout JSON)
      runtime [dir=PATH] [format=text]        is the configuration in effect? scan vs docker inspect (exit 0 all in effect, 1 not)
      remark <text>                           write a note into the history stream
      compare [dir=PATH] [ref=PATH|git:REF] [format=text]   differences from a reference folder, by meaning (exit 0 same, 1 different)
      scan dir=PATH [depth=6] [format=text]   recurse a directory: variables, ports, volumes and bind mounts per compose stack
      tools | workflows | args | vars list primitives / workflows / arg sets / resolved variables (stdout)
      spool [out=F] [last=N] [note=..] [withConfig=true] [scan=true] [scanDir=PATH] [compareRef=PATH] [defang=true] [file ...]   write one text file of context for a chat assistant

    --host NAME   run on a Linux server over ssh (needs ssh.target in globals.NAME.json)
    --dry-run     print each step's expanded arguments; execute nothing
    --read-only   refuse any step that would change the deployment (files, containers, images, the repo)
    """);
