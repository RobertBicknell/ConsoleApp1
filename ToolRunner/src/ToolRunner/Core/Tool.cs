using System.Text.Json;
using System.Text.Json.Nodes;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// One argument of a verb, as declared by its tool. Type: string | bool | int | list | object | edits.
/// Every type also accepts a string, because any value may be a `${...}` that is expanded at run time.
public sealed record Arg(string Name, string Type, string Doc, bool Required = false, string[]? Values = null);

/// Mutates: the verb changes the deployment (files, containers, images, the repo). Read-only mode refuses those.
public sealed record VerbSpec(string Verb, string Doc, IReadOnlyList<Arg> Args, bool Mutates = false);

/// Terse constructors so a tool's spec reads as a table.
public static class A
{
    public static Arg Str(string n, string d, bool req = false) => new(n, "string", d, req);
    public static Arg Bool(string n, string d) => new(n, "bool", d);
    public static Arg Int(string n, string d) => new(n, "int", d);
    public static Arg List(string n, string d, bool req = false) => new(n, "list", d, req);
    public static Arg Obj(string n, string d) => new(n, "object", d);
    public static Arg Enum(string n, string d, params string[] values) => new(n, "string", d, false, values);
    public static VerbSpec Verb(string verb, string doc, params Arg[][] groups) => new(verb, doc, groups.SelectMany(g => g).ToList());
    /// A verb that changes the deployment.
    public static VerbSpec Change(string verb, string doc, params Arg[][] groups) => new(verb, doc, groups.SelectMany(g => g).ToList(), Mutates: true);
    public static Arg[] Args(params Arg[] a) => a;
}

public interface ITool
{
    string Name { get; }
    /// What the tool accepts; drives verb validation and the generated JSON Schemas.
    IReadOnlyList<VerbSpec> Spec { get; }
    IReadOnlyList<string> Verbs => Spec.Select(s => s.Verb).ToList();
    Task<ToolResult> RunAsync(ToolContext ctx);
}

public sealed record ToolResult(bool Ok, string Message, Dictionary<string, string>? Outputs = null)
{
    public static ToolResult Success(string msg, Dictionary<string, string>? outputs = null) => new(true, msg, outputs);
    public static ToolResult Fail(string msg) => new(false, msg);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;
    public ToolRegistry(params ITool[] tools) => _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<ITool> All => _tools.Values.OrderBy(t => t.Name);
    public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name, out tool!);
}

/// Everything a tool sees: its (already expanded) args, the shared state, and the host to run on.
public sealed class ToolContext
{
    public required RunState State { get; init; }
    public required JsonObject Args { get; init; }
    public required IHost Host { get; init; }
    public required Action<string> Log { get; init; }
    public CancellationToken Ct { get; init; }

    public string Verb => Str("verb");

    public static string? NodeStr(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => n.ToJsonString()
    };

    public string? Opt(string key) => NodeStr(Args[key]);

    public string Str(string key) =>
        Opt(key) ?? throw new ArgumentException($"Missing required argument '{key}'");

    /// Fallback may itself contain ${...}; it is expanded against the shared state.
    public string Str(string key, string fallback) => Opt(key) ?? State.Expand(fallback);

    public bool Bool(string key, bool fallback) => Opt(key) is { } s ? bool.Parse(s) : fallback;
    public int Int(string key, int fallback) => Opt(key) is { } s ? int.Parse(s) : fallback;

    public List<string> List(string key) => Args[key] switch
    {
        null => new(),
        JsonArray a => a.Select(NodeStr).Where(s => s is not null).Select(s => s!).ToList(),
        var n => new() { NodeStr(n)! }
    };

    public IEnumerable<JsonObject> Objects(string key) =>
        Args[key] is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    /// A state variable, expanded; null if undefined.
    public string? Var(string name) => State.Vars.TryGetValue(name, out var v) ? State.Expand(v) : null;

    public Task<ProcResult> Exec(string exe, IEnumerable<string> args, string? workDir = null, bool echo = true) =>
        Host.ExecAsync(exe, args.ToList(), workDir, echo ? Log : null, Ct);
}
