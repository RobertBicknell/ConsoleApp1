using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner.Core;

/// The only code that writes config files. Everything it writes is reversible by hand: variable overrides go to the
/// personal layer (globals.user.json), never to globals.json; workflows and arg sets are added under their own key.
/// Files are re-serialised, so comments in a file it rewrites are lost (the shipped files have none; use "$comment").
public static class ConfigWriter
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject Load(string path) =>
        File.Exists(path) && JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is JsonObject o ? o : new JsonObject();

    private static void Save(string path, JsonObject root) =>
        File.WriteAllText(path, root.ToJsonString(ConfigLoader.JsonOptions) + "\n", new System.Text.UTF8Encoding(false));

    private static string UserFile(string dir) => Path.Combine(dir, "globals.user.json");

    /// Sets a variable in the personal layer. A dotted key (services.web.port) is written as nested objects.
    public static void SetUserVar(string dir, string key, string value)
    {
        var root = Load(UserFile(dir));
        var parts = key.Split('.');
        var o = root;
        foreach (var p in parts[..^1])
        {
            if (o[p] is not JsonObject child) o[p] = child = new JsonObject();      // a scalar in the way is replaced
            o = child;
        }
        o[parts[^1]] = value;
        Save(UserFile(dir), root);
    }

    /// Removes a personal override so the value from globals.json shows through again. True if it was there.
    public static bool RemoveUserVar(string dir, string key)
    {
        var root = Load(UserFile(dir));
        var parts = key.Split('.');
        var chain = new List<JsonObject> { root };
        foreach (var p in parts[..^1])
        {
            if (chain[^1][p] is not JsonObject child) return false;
            chain.Add(child);
        }
        if (!chain[^1].Remove(parts[^1])) return false;
        for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--) chain[i - 1].Remove(parts[i - 1]);   // prune empty parents
        Save(UserFile(dir), root);
        return true;
    }

    private static JsonObject StepNode(StepDef s)
    {
        var o = new JsonObject { ["run"] = s.Run };
        if (s.With is { Count: > 0 }) o["with"] = s.With.DeepClone();
        if (s.ContinueOnError) o["continueOnError"] = true;
        return o;
    }

    /// Adds or replaces one workflow in workflows.json.
    public static void SaveWorkflow(string dir, string name, WorkflowDef wf)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace)) throw new ArgumentException("A workflow name must be one word");
        var path = Path.Combine(dir, "workflows.json");
        var root = Load(path);
        if (root["workflows"] is not JsonObject all) root["workflows"] = all = new JsonObject();
        var node = new JsonObject { ["description"] = wf.Description };
        if (wf.Params.Count > 0) node["params"] = new JsonArray(wf.Params.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        if (wf.Defaults.Count > 0) node["defaults"] = new JsonObject(wf.Defaults.Select(d => KeyValuePair.Create(d.Key, (JsonNode?)JsonValue.Create(d.Value))));
        node["steps"] = new JsonArray(wf.Steps.Select(s => (JsonNode)StepNode(s)).ToArray());
        all[name] = node;
        Save(path, root);
    }

    public static bool DeleteWorkflow(string dir, string name)
    {
        var path = Path.Combine(dir, "workflows.json");
        var root = Load(path);
        if (root["workflows"] is not JsonObject all || !all.Remove(name)) return false;
        Save(path, root);
        return true;
    }

    /// Adds or replaces one named argument set (args.<tool>.<intent>) in args.json.
    public static void SaveArgSet(string dir, string tool, string intent, JsonObject args)
    {
        if (string.IsNullOrWhiteSpace(intent) || intent.Any(c => char.IsWhiteSpace(c) || c == '/')) throw new ArgumentException("A command name must be one word without '/'");
        var path = Path.Combine(dir, "args.json");
        var root = Load(path);
        if (root["args"] is not JsonObject all) root["args"] = all = new JsonObject();
        if (all[tool] is not JsonObject sets) all[tool] = sets = new JsonObject();
        sets[intent] = args.DeepClone();
        Save(path, root);
    }

    /// Workflow parameters implied by a chain: every ${name} its steps use that no variable defines and no earlier
    /// step is known to produce. Names with dots or computed parts are skipped (they resolve through other variables).
    /// Pass each step's full argument object (the named set merged with its overrides), since a named set such as
    /// compose/stop-service is what mentions ${service}.
    public static List<string> ImpliedParams(IEnumerable<JsonObject> stepArgs, RunState state, IEnumerable<string> produced)
    {
        var known = new HashSet<string>(state.Vars.Keys.Concat(produced), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o: foreach (var (_, v) in o) Walk(v); break;
                case JsonArray a: foreach (var v in a) Walk(v); break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(v.GetValue<string>(), @"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}"))
                        if (!m.Value.Contains(":-") && !known.Contains(m.Groups[1].Value) && !found.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                            found.Add(m.Groups[1].Value);
                    break;
            }
        }
        foreach (var a in stepArgs) Walk(a);
        return found;
    }
}
