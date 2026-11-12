using System.Text;
using ToolRunner.Core;
using ToolRunner.Tools;

namespace ToolRunner.Ui;

/// Runs one thing at a time (a workflow, a single command, or an ad-hoc chain of steps) on the selected host,
/// streaming its log lines as they happen. Uses the same WorkflowRunner and history journal as the command line.
/// Values the tools produce are kept in the Workspace, so a later run can use them (e.g. logs "since I started it").
public sealed class RunSession(LiveConfig live, Workspace ws)
{
    public bool Running { get; private set; }
    public string? Title { get; private set; }
    public int? Exit { get; private set; }
    public IReadOnlyList<string> Log { get { lock (_lines) return _lines.ToList(); } }
    public event Action? Changed;

    private readonly List<string> _lines = new();
    private CancellationTokenSource? _cts;

    public void Stop() => _cts?.Cancel();

    public Task RunWorkflowAsync(string workflow, IReadOnlyDictionary<string, string> vars, bool dryRun)
    {
        if (!live.Cfg.Workflows.TryGetValue(workflow, out var wf)) return Task.CompletedTask;
        var given = wf.Params.Select(p => vars.GetValueOrDefault(p, "")).ToList();
        return RunAsync($"{workflow} {string.Join(' ', given)}".Trim(), wf.Steps, vars, dryRun, [workflow, .. given], wf.Params, wf.Defaults);
    }

    /// `argv` is what the history journal records as the command, so a UI run reads like its command-line equivalent.
    public async Task RunAsync(string title, IReadOnlyList<StepDef> steps, IReadOnlyDictionary<string, string> vars, bool dryRun,
                               string[] argv, IReadOnlyList<string>? required = null, IReadOnlyDictionary<string, string>? defaults = null)
    {
        if (Running) return;
        lock (_lines) _lines.Clear();
        (Running, Exit, Title) = (true, null, title + (dryRun ? "  (dry run)" : ""));
        Changed?.Invoke();

        var startedAt = DateTimeOffset.UtcNow;
        var transcript = new StringBuilder();
        void Log(string s) { lock (_lines) _lines.Add(s); lock (transcript) transcript.AppendLine(s); Changed?.Invoke(); }

        _cts = new CancellationTokenSource();
        try
        {
            var state = ConfigLoader.LoadState(live.Dir, live.Host, []);
            foreach (var (k, v) in ws.State().Vars) state.Vars[k] = v;                    // this session's tool outputs
            if (defaults is not null) foreach (var (k, v) in defaults) state.Vars[k] = v;
            foreach (var (k, v) in vars) if (v.Length > 0) { state.Vars[k] = v; ws.Remember(k, v); }
            var missing = (required ?? []).Where(p => !state.Vars.ContainsKey(p)).ToList();
            if (missing.Count > 0) throw new ArgumentException($"Missing value for: {string.Join(", ", missing)}");

            var cfg = ConfigLoader.LoadConfig(live.Dir);
            var host = ConfigLoader.CreateHost(state, live.Host);
            Log($"config: {live.Dir}   host: {host.Name}");
            var ok = await new WorkflowRunner(Registry.Default(), cfg, Log).RunAsync(steps, state, host, dryRun, _cts.Token, ws.ReadOnly);
            Exit = ok ? 0 : 1;
            if (!dryRun)
            {
                History.Append(live.Dir, new RunRecord(startedAt, host.Name, argv, Exit.Value, transcript.ToString(), new(state.Produced)));
                ws.Absorb(state.Produced);
            }
        }
        catch (OperationCanceledException) { Log("cancelled"); Exit = 1; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            Log($"error: {ex.Message}"); Exit = 2;
        }
        finally { Running = false; Changed?.Invoke(); }
    }
}
