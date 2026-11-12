using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Core;

/// The shared context every tool reads and writes. Values are strings so state is trivially
/// inspectable and serialisable. Names are case-insensitive; dots express hierarchy (services.pluginhost.image).
[System.Diagnostics.DebuggerDisplay("{Vars.Count} vars, {Produced.Count} produced by tools")]
[System.Diagnostics.DebuggerTypeProxy(typeof(RunStateView))]
public sealed class RunState
{
    public Dictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Produced { get; } = new(StringComparer.OrdinalIgnoreCase); // tool outputs only

    // ${NAME} or ${NAME:-default}, same shape as .env / docker compose interpolation.
    private static readonly Regex Var = new(@"\$\{([\w.\-]+)(?::-([^}]*))?\}", RegexOptions.Compiled);

    public void Set(string key, string value) { Vars[key] = value; Produced[key] = value; }

    /// Expands repeatedly, so values can reference values (a hierarchy) and names can be computed:
    /// ${services.${service}.image} resolves the inner ${service} first.
    public string Expand(string s)
    {
        for (var pass = 0; pass < 10; pass++)
        {
            var next = Var.Replace(s, m =>
            {
                var key = m.Groups[1].Value;
                if (Vars.TryGetValue(key, out var v)) return v;
                if (m.Groups[2].Success) return m.Groups[2].Value;
                throw new InvalidOperationException($"Undefined variable ${{{key}}}");
            });
            if (next == s) return s;
            s = next;
        }
        throw new InvalidOperationException($"Variable expansion did not converge (cycle?): {s}");
    }

    public JsonNode? ExpandNode(JsonNode? n) => n switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, ExpandNode(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(ExpandNode).ToArray()),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => JsonValue.Create(Expand(v.GetValue<string>())),
        _ => n.DeepClone()
    };
}

/// Watch-window view of RunState: expand `state` to see every variable already expanded, sorted, plus tool outputs.
/// A value that cannot expand (undefined reference, cycle) shows the error text instead of throwing.
internal sealed class RunStateView(RunState s)
{
    public KeyValuePair<string, string>[] Resolved => s.Vars.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
        .Select(k => KeyValuePair.Create(k.Key, Try(k.Value))).ToArray();
    public KeyValuePair<string, string>[] ProducedByTools => s.Produced.OrderBy(k => k.Key).ToArray();
    public KeyValuePair<string, string>[] Raw => s.Vars.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    private string Try(string v) { try { return s.Expand(v); } catch (InvalidOperationException e) { return $"<{e.Message}>"; } }
}
