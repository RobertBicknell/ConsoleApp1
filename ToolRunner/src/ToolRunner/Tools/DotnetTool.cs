using ToolRunner.Core;

namespace ToolRunner.Tools;

/// Your own .NET functions as commands. Every public class in the plugins folder that implements IDotnetTool is a
/// verb of this tool, named after the class; its [Args] names are the verb's arguments, passed to ExecAsync in that
/// order; the dictionary it returns becomes variables for the following steps. `list` shows what was found.
/// A command counts as changing the deployment unless its class carries [ReadOnly].
public sealed class DotnetTool : ITool
{
    public string Name => "dotnet";

    public IReadOnlyList<VerbSpec> Spec =>
        new[] { A.Verb("list", "List the commands found in the plugins folder, and anything that could not be loaded.") }
            .Concat(DotnetPlugins.Load().Commands.Select(k => new VerbSpec(k.Name,
                $"{(k.Doc.Length > 0 ? k.Doc + " " : "")}[{k.Assembly}.dll] returns variables.",
                k.Args.Select(a => new Arg(a, "string", $"Argument {k.Args.ToList().IndexOf(a) + 1} of {k.Name}.ExecAsync.", Required: true)).ToList(),
                Mutates: !k.ReadOnly)))
            .ToList();

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        var set = DotnetPlugins.Load();
        if (c.Verb == "list")
        {
            c.Log($"plugins folder: {set.Dir}");
            foreach (var k in set.Commands) c.Log($"{k.Name}({string.Join(", ", k.Args)})   {(k.ReadOnly ? "reads" : "changes")}   {k.Assembly}.dll   {k.Doc}");
            foreach (var n in set.Notes) c.Log($"note: {n}");
            return ToolResult.Success($"{set.Commands.Count} command(s) in {set.Dir}");
        }
        var command = set.Commands.FirstOrDefault(k => k.Name.Equals(c.Verb, StringComparison.OrdinalIgnoreCase));
        if (command is null) return ToolResult.Fail($"no command '{c.Verb}' in {set.Dir}");

        var args = command.Args.Select(a => c.Args[a] is null ? "" : ToolContext.NodeStr(c.Args[a])!).ToArray();
        try
        {
            var outputs = await DotnetPlugins.RunAsync(command, args);
            foreach (var (k, v) in outputs) c.Log($"{k} = {(v.Length > 200 ? v[..200] + "..." : v)}");
            return ToolResult.Success($"{command.Name} returned {outputs.Count} value(s)", outputs);
        }
        catch (InvalidOperationException e) { return ToolResult.Fail(e.Message); }
    }
}
