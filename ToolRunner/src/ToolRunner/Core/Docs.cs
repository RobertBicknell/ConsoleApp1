using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ToolRunner.Core;

/// Fills the documentation template (config/docs.template.html) with reference tables read from the running tool:
/// tools and verbs, saved commands, workflows, variables, and the settings of the build and pipelines features.
/// The prose is hand-written; whatever can go stale is generated.
public static class Docs
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Mark(bool changes) => changes ? "<span class=\"tag changes\">changes</span>" : "<span class=\"tag reads\">reads</span>";

    public static string Generate(string template, ToolRegistry tools, ConfigSet cfg) => template
        .Replace("<!--GENERATED:TOOLS-->", Tools(tools))
        .Replace("<!--GENERATED:COMMANDS-->", Commands(tools, cfg))
        .Replace("<!--GENERATED:WORKFLOWS-->", Workflows(cfg))
        .Replace("<!--GENERATED:VARIABLES-->", Variables(tools, cfg))
        .Replace("<!--GENERATED:BUILD-->", Settings(BuildSettings.Spec))
        .Replace("<!--GENERATED:PIPELINE-->", Settings(PipelineSettings.Spec))
        .Replace("<!--GENERATED:STAMP-->", $"{tools.All.Count()} tools, {tools.All.Sum(t => t.Spec.Count)} verbs, {cfg.Args.Sum(a => a.Value.Count)} saved commands, {cfg.Workflows.Count} workflows");

    private static string Tools(ToolRegistry tools)
    {
        var sb = new StringBuilder();
        foreach (var t in tools.All)
        {
            sb.Append($"<details class=\"sub\" id=\"tool-{H(t.Name)}\"><summary><code>{H(t.Name)}</code> <span class=\"dim\">{string.Join(", ", t.Spec.Select(v => H(v.Verb)))}</span></summary>\n");
            foreach (var v in t.Spec)
            {
                sb.Append($"<h4><code>{H(t.Name)} {H(v.Verb)}</code> {Mark(v.Mutates)}</h4><p>{H(v.Doc)}</p>\n");
                if (v.Args.Count == 0) continue;
                sb.Append("<table><tr><th>argument</th><th>type</th><th>meaning</th></tr>\n");
                foreach (var a in v.Args)
                    sb.Append($"<tr><td><code>{H(a.Name)}</code>{(a.Required ? " <span class=\"tag changes\">required</span>" : "")}</td><td>{H(a.Type)}{(a.Values is { Length: > 0 } vals ? "<br><span class=\"dim\">" + H(string.Join(" | ", vals)) + "</span>" : "")}</td><td>{H(a.Doc)}</td></tr>\n");
                sb.Append("</table>\n");
            }
            sb.Append("</details>\n");
        }
        return sb.ToString();
    }

    private static string Commands(ToolRegistry tools, ConfigSet cfg)
    {
        var sb = new StringBuilder("<table><tr><th>saved command</th><th>runs</th><th>arguments</th></tr>\n");
        foreach (var (tool, sets) in cfg.Args.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var (name, set) in sets)
            {
                var verb = ToolContext.NodeStr(set["verb"]);
                var changes = !tools.TryGet(tool, out var t) || (t.Spec.FirstOrDefault(v => v.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase))?.Mutates ?? true);
                var rest = new JsonObject(set.Where(kv => kv.Key != "verb").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
                sb.Append($"<tr><td><code>{H(tool)}/{H(name)}</code></td><td><code>{H(tool)} {H(verb)}</code> {Mark(changes)}</td><td><code>{H(rest.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))}</code></td></tr>\n");
            }
        return sb.Append("</table>\n").ToString();
    }

    private static string Workflows(ConfigSet cfg)
    {
        var sb = new StringBuilder("<table><tr><th>workflow</th><th>what it does</th><th>steps</th></tr>\n");
        foreach (var (name, w) in cfg.Workflows.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append($"<tr><td><code>{H(name)}{string.Concat(w.Params.Select(p => " &lt;" + H(p) + "&gt;"))}</code></td><td>{H(w.Description)}</td><td>{string.Join("<br>", w.Steps.Select(s => "<code>" + H(s.Run) + "</code>" + (s.ContinueOnError ? " <span class=\"dim\">(carries on if it fails)</span>" : "")))}</td></tr>\n");
        return sb.Append("</table>\n").ToString();
    }

    private static string Variables(ToolRegistry tools, ConfigSet cfg)
    {
        var schema = Schemas.Generate(tools, cfg).GetValueOrDefault("globals.schema.json");
        var sb = new StringBuilder("<table><tr><th>variable</th><th>meaning</th></tr>\n");
        if (schema is not null && JsonNode.Parse(schema)?["properties"] is JsonObject props)
            foreach (var (name, node) in props.Where(p => !p.Key.StartsWith('$') && !p.Key.StartsWith("BUILD_") && !p.Key.StartsWith("PIPELINE_")))
                sb.Append($"<tr><td><code>{H(name)}</code></td><td>{H(ToolContext.NodeStr(node?["description"]))}</td></tr>\n");
        return sb.Append("</table>\n").ToString();
    }

    private static string Settings((string Var, string Label, string Help, string Default, string Group)[] spec)
    {
        var sb = new StringBuilder("<table><tr><th>variable</th><th>what</th><th>meaning</th><th>default</th></tr>\n");
        foreach (var s in spec) sb.Append($"<tr><td><code>{H(s.Var)}</code></td><td>{H(s.Label)}<br><span class=\"dim\">{H(s.Group)}</span></td><td>{H(s.Help)}</td><td>{(s.Default.Length > 0 ? "<code>" + H(s.Default) + "</code>" : "")}</td></tr>\n");
        return sb.Append("</table>\n").ToString();
    }
}
