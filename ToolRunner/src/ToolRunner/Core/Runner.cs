using System.Text.Json.Nodes;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

public sealed class WorkflowRunner(ToolRegistry tools, ConfigSet cfg, Action<string> log)
{
    private sealed record Resolved(ITool Tool, JsonObject Args);

    // one line, and readable: no \u003C for '<' or \u0027 for quotes
    private static readonly System.Text.Json.JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// Resolves "tool/intent" + inline overrides into a tool and its (not yet expanded) argument object.
    private Resolved Resolve(StepDef step)
    {
        var parts = step.Run.Split('/', 2);
        if (!tools.TryGet(parts[0], out var tool))
            throw new ArgumentException($"Unknown tool '{parts[0]}' in step '{step.Run}'. Tools: {string.Join(", ", tools.All.Select(t => t.Name))}");

        JsonObject args;
        if (parts.Length == 2)
        {
            if (!cfg.Args.TryGetValue(parts[0], out var sets) || !sets.TryGetValue(parts[1], out var set))
                throw new ArgumentException($"No arg set '{step.Run}' in args.json. " +
                    $"Available for '{parts[0]}': {(cfg.Args.TryGetValue(parts[0], out var s) ? string.Join(", ", s.Keys) : "none")}");
            args = (JsonObject)set.DeepClone();
        }
        else args = new JsonObject();

        if (step.With is not null)
            foreach (var (k, v) in step.With) args[k] = v?.DeepClone();

        // Catch typos in a literal verb before anything runs (verbs containing ${} are checked at run time).
        if (ToolContext.NodeStr(args["verb"]) is { } verb && !verb.Contains("${") &&
            !tool.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Step '{step.Run}': tool '{tool.Name}' has no verb '{verb}'. Verbs: {string.Join(", ", tool.Verbs)}");
        if (args["verb"] is null)
            throw new ArgumentException($"Step '{step.Run}': no 'verb' given. Verbs for '{tool.Name}': {string.Join(", ", tool.Verbs)}");

        return new Resolved(tool, args);
    }

    public async Task<bool> RunAsync(IReadOnlyList<StepDef> steps, RunState state, IHost host, bool dryRun, CancellationToken ct, bool readOnly = false)
    {
        // Validate every step up front: a typo in step 6 must not be discovered after step 5 changed something.
        var resolved = steps.Select(Resolve).ToList();

        // Read-only mode: refuse the whole run before anything happens if any step would change the deployment.
        // (A dry run executes nothing, so it is always allowed.)
        if (readOnly && !dryRun)
        {
            var blocked = steps.Zip(resolved).Where(x => Changes(x.Second.Tool, ToolContext.NodeStr(x.Second.Args["verb"]))).ToList();
            foreach (var (step, r) in blocked)
                log($"BLOCKED {step.Run}: {r.Tool.Name} {ToolContext.NodeStr(r.Args["verb"])} changes the deployment, and read-only mode is on");
            if (blocked.Count > 0) return false;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var (step, r) = (steps[i], resolved[i]);
            log($"[{i + 1}/{steps.Count}] {step.Run}");

            if (dryRun) { log($"    WOULD RUN {Describe(r.Args, state)}"); continue; }

            ToolResult result;
            try
            {
                var args = (JsonObject)state.ExpandNode(r.Args)!;
                // a verb computed from ${...} is only known now
                if (readOnly && Changes(r.Tool, ToolContext.NodeStr(args["verb"])))
                    throw new InvalidOperationException($"blocked: {r.Tool.Name} {ToolContext.NodeStr(args["verb"])} changes the deployment, and read-only mode is on");
                log($"    RAN {args.ToJsonString(Compact)}");      // what it actually ran with, the counterpart of a dry run's WOULD RUN
                result = await r.Tool.RunAsync(new ToolContext
                {
                    State = state, Args = args, Host = host, Ct = ct, Log = l => log($"    {l}")
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = ToolResult.Fail(ex.Message);
            }

            log($"    {(result.Ok ? "OK  " : "FAIL")} {result.Message}");
            if (result.Ok && result.Outputs is not null)
                foreach (var (k, v) in result.Outputs) state.Set(k, v);

            if (!result.Ok && !step.ContinueOnError) return false;
        }
        return true;
    }

    private static bool Changes(ITool tool, string? verb) =>
        tool.Spec.FirstOrDefault(v => v.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase))?.Mutates == true;

    private static string Describe(JsonObject args, RunState state)
    {
        try { return state.ExpandNode(args)!.ToJsonString(Compact); }
        catch (InvalidOperationException) { return args.ToJsonString(Compact) + "   (some values come from earlier steps)"; }
    }
}
