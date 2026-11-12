using ToolRunner.Core;

namespace ToolRunner.Ui;

/// The config directory as it is right now. Watches the config files and the run history; any change reloads
/// (debounced, since editors write a file in several steps) and raises Changed. A file that fails to parse
/// mid-edit leaves the last good snapshot in place and sets Error.
public sealed class LiveConfig : IDisposable
{
    public string Dir { get; }
    public RunState State { get; private set; } = new();
    public ConfigSet Cfg { get; private set; } = new();
    public string? Host { get; private set; }          // null = local; else the name of globals.<name>.json
    public string? Error { get; private set; }
    /// Which layer ("globals", "user", or a host name) each variable's current value comes from.
    public IReadOnlyDictionary<string, string> Origins { get; private set; } = new Dictionary<string, string>();

    /// The tool wins: edits go to the personal layer only; the file watcher then reloads, so the page shows what the tool resolved.
    public void SetVar(string key, string value) => ConfigWriter.SetUserVar(Dir, key, value);
    public bool ResetVar(string key) => ConfigWriter.RemoveUserVar(Dir, key);
    public DateTimeOffset LoadedAt { get; private set; }
    public event Action? Changed;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _debounce;

    public LiveConfig(string dir)
    {
        Dir = dir;
        _debounce = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        Reload();
        Directory.CreateDirectory(History.Dir(dir));
        foreach (var d in new[] { dir, History.Dir(dir) })
        {
            var w = new FileSystemWatcher(d) { EnableRaisingEvents = true, IncludeSubdirectories = false };
            w.Changed += (_, _) => Kick(); w.Created += (_, _) => Kick(); w.Deleted += (_, _) => Kick(); w.Renamed += (_, _) => Kick();
            _watchers.Add(w);
        }
    }

    /// Hosts offered in the picker: every globals.<name>.json except the per-user override.
    public IReadOnlyList<string> Hosts() => Directory.GetFiles(Dir, "globals.*.json")
        .Select(f => Path.GetFileNameWithoutExtension(f)["globals.".Length..]).Where(n => n != "user").Order().ToList();

    public void SelectHost(string? host) { Host = string.IsNullOrEmpty(host) ? null : host; Reload(); }

    public ToolRunner.Hosts.IHost CreateHost() => ConfigLoader.CreateHost(State, Host);

    private void Kick() => _debounce.Change(300, Timeout.Infinite);

    public void Reload()
    {
        try
        {
            State = ConfigLoader.LoadState(Dir, Host, []);
            Cfg = ConfigLoader.LoadConfig(Dir);
            Origins = ConfigLoader.LoadLayers(Dir, Host)
                .SelectMany(l => l.Vars.Keys.Select(k => (k, l.Layer)))
                .GroupBy(x => x.k, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Layer, StringComparer.OrdinalIgnoreCase);
            Schemas.Write(Dir, ToolRunner.Tools.Registry.Default(), Cfg);      // keep editor schemas in step with args.json (writes only on change)
            Error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            Error = ex.Message;
        }
        LoadedAt = DateTimeOffset.Now;
        Changed?.Invoke();
    }

    public void Dispose() { foreach (var w in _watchers) w.Dispose(); _debounce.Dispose(); }
}
