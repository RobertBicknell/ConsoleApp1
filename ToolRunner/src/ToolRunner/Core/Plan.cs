using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Core;

/// What an assistant proposed: steps to run (already expanded from workflows), the variables they need, and what
/// should be visible afterwards if the intent is met. Problems lists everything that does not exist or does not fit;
/// a plan with problems is shown, not run.
public sealed record Plan(string? Intent, Dictionary<string, string> Vars, List<StepDef> Steps, string? Expect, List<string> Problems, bool Changes);

/// The plan cycle with a chat assistant that cannot run anything itself:
/// STATE -> INTENT -> OPTIONS -> PLAN -> ACTION -> OUTCOME. This class writes OPTIONS (what exists to choose from)
/// and reads PLAN (the assistant's choice) back into steps the runner executes.
public static class Planning
{
    private static bool Mutates(ToolRegistry tools, string tool, string? verb) =>
        !tools.TryGet(tool, out var t) || verb is null || verb.Contains("${") || (t.Spec.FirstOrDefault(v => v.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase))?.Mutates ?? true);

    private static (string Tool, JsonObject? Args) Resolve(ConfigSet cfg, StepDef step)
    {
        var parts = step.Run.Split('/', 2);
        var args = new JsonObject();
        if (parts.Length == 2)
        {
            if (!cfg.Args.TryGetValue(parts[0], out var sets) || !sets.TryGetValue(parts[1], out var set)) return (parts[0], null);
            foreach (var (k, v) in set) args[k] = v?.DeepClone();
        }
        if (step.With is not null) foreach (var (k, v) in step.With) args[k] = v?.DeepClone();
        return (parts[0], args);
    }

    private static bool StepChanges(ToolRegistry tools, ConfigSet cfg, StepDef s) =>
        Resolve(cfg, s) is var (tool, args) && (args is null || Mutates(tools, tool, ToolContext.NodeStr(args["verb"])));

    /// Everything that can be chosen, as plain lines: workflows, saved commands, tool verbs. Each is marked
    /// [reads] or [CHANGES], and says which values it needs.
    public static string Options(ToolRegistry tools, ConfigSet cfg, RunState state)
    {
        var sb = new StringBuilder();
        string Mark(bool changes) => changes ? "[CHANGES]" : "[reads]  ";
        List<string> Needs(IEnumerable<StepDef> steps) => ConfigWriter.ImpliedParams(steps.Select(s => Resolve(cfg, s).Args).OfType<JsonObject>(), state, []);

        sb.AppendLine("WORKFLOWS: a named sequence of steps. Use as {\"workflow\": \"NAME\", \"params\": {\"PARAM\": \"value\"}}.");
        foreach (var (name, w) in cfg.Workflows.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            var needs = w.Params.Concat(Needs(w.Steps)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sb.AppendLine($"  {Mark(w.Steps.Any(s => StepChanges(tools, cfg, s)))} {name}   params: {(needs.Count == 0 ? "-" : string.Join(", ", needs))}");
            sb.AppendLine($"              {w.Description}");
            sb.AppendLine($"              steps: {string.Join(" > ", w.Steps.Select(s => s.Run))}");
        }

        sb.AppendLine();
        sb.AppendLine("SAVED COMMANDS: one tool call with its arguments already filled in. Use as {\"run\": \"TOOL/NAME\"}; add \"with\": {...} to override an argument. Values it needs go in \"vars\".");
        foreach (var (tool, sets) in cfg.Args.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var (name, set) in sets)
            {
                var verb = ToolContext.NodeStr(set["verb"]);
                var needs = Needs([new StepDef { Run = $"{tool}/{name}" }]);
                sb.AppendLine($"  {Mark(Mutates(tools, tool, verb))} {tool}/{name}   = {tool} {verb}   needs: {(needs.Count == 0 ? "-" : string.Join(", ", needs))}");
            }

        sb.AppendLine();
        sb.AppendLine("TOOL VERBS: the raw operations. Use as {\"run\": \"TOOL\", \"with\": {\"verb\": \"VERB\", \"ARG\": value}}. Arguments marked * are required. A value may be ${VARIABLE}.");
        foreach (var t in tools.All)
            foreach (var v in t.Spec)
            {
                sb.AppendLine($"  {Mark(v.Mutates)} {t.Name} {v.Verb}   {v.Doc}");
                if (v.Args.Count > 0) sb.AppendLine($"              args: {string.Join(", ", v.Args.Select(a => $"{a.Name}{(a.Required ? "*" : "")}{(a.Type == "string" ? "" : ":" + a.Type)}"))}");
            }
        return sb.ToString();
    }

    /// The extra spool section for plan mode.
    public static (string Title, string Body) OptionsSection(ToolRegistry tools, ConfigSet cfg, RunState state) =>
        ("OPTIONS (everything toolrunner can do; nothing else exists. [reads] never alters the deployment, [CHANGES] does)", Options(tools, cfg, state));

    /// The JSON object in a reply: inside a ``` fence if there is one, else the first balanced {...} that has "steps".
    public static string? ExtractJson(string reply)
    {
        var text = reply.Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'');
        foreach (Match m in Regex.Matches(text, @"```[A-Za-z]*\s*(\{.*?\})\s*```", RegexOptions.Singleline))
            if (m.Groups[1].Value.Contains("\"steps\"")) return m.Groups[1].Value;
        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            int depth = 0; var inString = false;
            for (var i = start; i < text.Length; i++)
            {
                var ch = text[i];
                if (inString) { if (ch == '\\') i++; else if (ch == '"') inString = false; continue; }
                if (ch == '"') inString = true;
                else if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    var candidate = text[start..(i + 1)];
                    if (candidate.Contains("\"steps\"")) return candidate;
                    break;
                }
            }
        }
        return null;
    }

    /// Reads a reply into a plan and checks every name in it against what exists.
    public static Plan Parse(string reply, ToolRegistry tools, ConfigSet cfg, RunState state)
    {
        var problems = new List<string>();
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<StepDef>();
        Plan Done(string? intent = null, string? expect = null) => new(intent, vars, steps, expect, problems, steps.Any(s => StepChanges(tools, cfg, s)));

        if (ExtractJson(reply) is not { } json) { problems.Add("no plan found: the reply has no JSON object with \"steps\""); return Done(); }
        JsonObject root;
        try { root = (JsonObject)JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!; }
        catch (Exception e) when (e is JsonException or InvalidCastException) { problems.Add($"the plan is not valid JSON: {e.Message}"); return Done(); }

        void SetVar(string k, string v, string from)
        {
            if (vars.TryGetValue(k, out var had) && had != v) problems.Add($"{from}: '{k}' is given two different values ({had}, {v}); one plan can hold one value per name");
            vars[k] = v;
        }
        if (root["vars"] is JsonObject vo) foreach (var (k, v) in vo) SetVar(k, ToolContext.NodeStr(v) ?? "", "vars");

        var n = 0;
        foreach (var node in root["steps"] as JsonArray ?? [])
        {
            n++;
            if (node is not JsonObject o) { problems.Add($"step {n}: not an object"); continue; }
            if (ToolContext.NodeStr(o["workflow"]) is { } wfName)
            {
                if (!cfg.Workflows.TryGetValue(wfName, out var wf)) { problems.Add($"step {n}: no workflow '{wfName}'"); continue; }
                if (o["params"] is JsonObject po) foreach (var (k, v) in po) SetVar(k, ToolContext.NodeStr(v) ?? "", $"step {n} ({wfName})");
                foreach (var (k, v) in wf.Defaults) if (!vars.ContainsKey(k)) vars[k] = v;
                steps.AddRange(wf.Steps.Select(s => new StepDef { Run = s.Run, With = s.With?.DeepClone() as JsonObject, ContinueOnError = s.ContinueOnError }));
                continue;
            }
            if (ToolContext.NodeStr(o["run"]) is not { Length: > 0 } run) { problems.Add($"step {n}: needs \"run\" or \"workflow\""); continue; }
            var step = new StepDef { Run = run, With = o["with"]?.DeepClone() as JsonObject, ContinueOnError = o["continueOnError"]?.GetValueKind() == JsonValueKind.True };
            steps.Add(step);

            var (toolName, args) = Resolve(cfg, step);
            if (!tools.TryGet(toolName, out var tool)) { problems.Add($"step {n}: no tool '{toolName}'"); continue; }
            if (args is null) { problems.Add($"step {n}: no saved command '{run}'"); continue; }
            if (ToolContext.NodeStr(args["verb"]) is not { } verb) { problems.Add($"step {n}: {run} has no \"verb\""); continue; }
            if (verb.Contains("${")) continue;
            if (tool.Spec.FirstOrDefault(v => v.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase)) is not { } spec) { problems.Add($"step {n}: {toolName} has no verb '{verb}'"); continue; }
            foreach (var (k, _) in args) if (k != "verb" && spec.Args.All(a => a.Name != k)) problems.Add($"step {n}: {toolName} {verb} has no argument '{k}'");
            foreach (var a in spec.Args.Where(a => a.Required && args[a.Name] is null)) problems.Add($"step {n}: {toolName} {verb} needs '{a.Name}'");
        }

        // values nothing supplies
        var probe = new RunState();
        foreach (var (k, v) in state.Vars) probe.Vars[k] = v;
        foreach (var (k, v) in vars) probe.Vars[k] = v;
        foreach (var missing in ConfigWriter.ImpliedParams(steps.Select(s => Resolve(cfg, s).Args).OfType<JsonObject>(), probe, KnownOutputs))
            problems.Add($"needs a value for '{missing}' (not in \"vars\", not a configured variable)");

        return Done(ToolContext.NodeStr(root["intent"]), ToolContext.NodeStr(root["expect"]));
    }

    /// Variables that steps write for later steps; a plan that reads them is not asked to supply them.
    public static readonly string[] KnownOutputs =
        { "STARTED_AT_UTC", "GIT_HEAD", "GIT_DIRTY_COUNT", "DIFF_LINES", "SOURCE_COMMIT", "SOURCE_REF", "IMAGE", "LOG_LINES", "LOG_MATCHES", "LOG_FIRST_MATCH",
          "DOCKER_PS", "DOCKER_INSPECT", "DOCKER_HISTORY", "DOCKER_RUN", "PLUGINS_MISSING", "PLUGINS_LISTED", "VERSION_DETECTED", "VERSION_VARIABLE",
          "DEBUG_SERVICE", "DEBUG_IMAGE", "DEBUG_BASE_IMAGE", "DEBUG_DOCKERFILE", "DEBUG_CONTEXT", "DEBUG_SOURCE_ROOT", "DEBUG_VSDBG_MOUNT", "DEBUG_VSDBG_PATH", "DEBUG_VERSION" };

    public static IEnumerable<string> Describe(Plan p)
    {
        if (p.Intent is not null) yield return $"intent: {p.Intent}";
        foreach (var (k, v) in p.Vars) yield return $"  {k} = {v}";
        for (var i = 0; i < p.Steps.Count; i++)
            yield return $"  {i + 1}. {p.Steps[i].Run}{(p.Steps[i].With is { } w ? " " + w.ToJsonString() : "")}";
        if (p.Expect is not null) yield return $"expect: {p.Expect}";
        yield return p.Changes ? "this plan CHANGES the deployment" : "this plan only reads";
        foreach (var x in p.Problems) yield return $"PROBLEM: {x}";
    }
}
