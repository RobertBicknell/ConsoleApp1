using System.Text.Json;
using System.Text.Json.Nodes;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

// ---------- JSON-backed definitions ----------

public sealed class StepDef
{
    public string Run { get; set; } = "";               // "tool/intent" (named arg set) or just "tool"
    public JsonObject? With { get; set; }               // inline overrides merged over the named arg set
    public bool ContinueOnError { get; set; }
}

public sealed class WorkflowDef
{
    public string Description { get; set; } = "";
    public List<string> Params { get; set; } = new();   // positional parameter names
    public Dictionary<string, string> Defaults { get; set; } = new();
    public List<StepDef> Steps { get; set; } = new();
}

public sealed class ConfigSet
{
    /// args[tool][intent] -> argument object
    public Dictionary<string, Dictionary<string, JsonObject>> Args { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WorkflowDef> Workflows { get; } = new(StringComparer.OrdinalIgnoreCase);
}

file sealed class ArgsFile { public Dictionary<string, Dictionary<string, JsonObject>> Args { get; set; } = new(); }
file sealed class WorkflowFile { public Dictionary<string, WorkflowDef> Workflows { get; set; } = new(); }

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static JsonSerializerOptions JsonOptions => Opts;

    public static string ResolveDir(string? explicitDir)
    {
        var candidates = new[]
        {
            explicitDir,
            Environment.GetEnvironmentVariable("TOOLRUNNER_CONFIG"),
            Path.Combine(Directory.GetCurrentDirectory(), "config"),
            Path.Combine(AppContext.BaseDirectory, "config")
        };
        foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
            if (Directory.Exists(c)) return Path.GetFullPath(c!);
        throw new ArgumentException("No config directory found (use --config DIR, TOOLRUNNER_CONFIG, or ./config)");
    }

    /// Each globals layer that exists, in the order it is applied, with its own flattened values. The layer name
    /// ("globals", "user", or the host name) is what the UI shows as a variable's origin.
    public static IReadOnlyList<(string Layer, Dictionary<string, string> Vars)> LoadLayers(string dir, string? host)
    {
        // The host layer comes AFTER the user layer on purpose: personal local-machine values
        // (e.g. your own APP_ROOT) must never shadow the target server's values.
        var files = new List<(string Layer, string File)> { ("globals", "globals.json"), ("user", "globals.user.json") };
        if (host is not null) files.Add((host, $"globals.{host}.json"));

        var layers = new List<(string, Dictionary<string, string>)>();
        foreach (var (layer, file) in files)
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten("", node, flat);
            layers.Add((layer, flat));
        }
        return layers;
    }

    /// Layers, later wins: globals.json < globals.user.json < globals.<host>.json < --set KEY=VALUE.
    /// (Same idea as appsettings.json / appsettings.<Environment>.json.)
    public static RunState LoadState(string dir, string? host, IEnumerable<string> sets)
    {
        var state = new RunState();
        foreach (var (_, vars) in LoadLayers(dir, host))
            foreach (var (k, v) in vars) state.Vars[k] = v;

        foreach (var s in sets)
        {
            var eq = s.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"--set expects KEY=VALUE, got '{s}'");
            state.Vars[s[..eq]] = s[(eq + 1)..];
        }
        return state;
    }

    private static void Flatten(string prefix, JsonNode? n, Dictionary<string, string> into)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o.Where(p => !p.Key.StartsWith('$')))      // $schema, $comment: editor metadata, not variables
                    Flatten(prefix == "" ? k : $"{prefix}.{k}", v, into);
                break;
            case JsonArray:
                throw new InvalidOperationException($"globals: arrays are not supported ('{prefix}'); put lists in args.json");
            case JsonValue v:
                into[prefix] = ToolContext.NodeStr(v)!;
                break;
            default:
                into[prefix] = "";
                break;
        }
    }

    public static ConfigSet LoadConfig(string dir)
    {
        var cfg = new ConfigSet();

        var argsPath = Path.Combine(dir, "args.json");
        if (File.Exists(argsPath))
            foreach (var (tool, intents) in JsonSerializer.Deserialize<ArgsFile>(File.ReadAllText(argsPath), Opts)!.Args)
                cfg.Args[tool] = new Dictionary<string, JsonObject>(intents, StringComparer.OrdinalIgnoreCase);

        var wfPath = Path.Combine(dir, "workflows.json");
        if (File.Exists(wfPath))
            foreach (var (name, wf) in JsonSerializer.Deserialize<WorkflowFile>(File.ReadAllText(wfPath), Opts)!.Workflows)
                cfg.Workflows[name] = wf;

        return cfg;
    }

    public static IHost CreateHost(RunState state, string? hostName)
    {
        // CONTAINER_ENGINE: every "docker ..." the tools run goes to this program instead (podman has the same command line)
        var engine = state.Vars.TryGetValue("CONTAINER_ENGINE", out var e) && state.Expand(e) is { Length: > 0 } named ? named : "docker";
        if (hostName is null) return new LocalHost { Engine = engine };
        string Need(string key) => state.Vars.TryGetValue(key, out var v) ? state.Expand(v)
            : throw new ArgumentException($"--host {hostName}: set '{key}' in globals.{hostName}.json");
        string? Maybe(string key) => state.Vars.TryGetValue(key, out var v) ? state.Expand(v) : null;

        var port = Maybe("ssh.port") is { } p ? int.Parse(p) : (int?)null;
        var opts = Maybe("ssh.options")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SshHost(Need("ssh.target"), port, Maybe("ssh.identity"), opts) { Engine = engine };
    }
}
