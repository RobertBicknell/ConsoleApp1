using System.Text.RegularExpressions;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// Guards against the three things that go stale silently: tool specs vs tool code, shipped config vs the engine,
/// and the generated JSON Schemas vs both.
public class ConfigIntegrityTests
{
    private static readonly Regex Reads = new(@"(?:c\.(?:Opt|Str|Bool|Int|List|Objects)\(|\bVal\(c, )""(\w+)""|c\.Args\[""(\w+)""\]", RegexOptions.Compiled);
    private static readonly Regex Declares = new(@"A\.(?:Str|Bool|Int|List|Obj|Enum)\(""(\w+)""|new Arg\(""(\w+)""", RegexOptions.Compiled);

    private static string[] Names(Regex rx, string text) =>
        rx.Matches(text).SelectMany(m => m.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => g.Value)).Distinct().Order().ToArray();

    public static TheoryData<string> ToolFiles() =>
        new(Directory.GetFiles(Repo.SourceDir("Tools"), "*Tool.cs").Select(Path.GetFileName)!);

    [Theory, MemberData(nameof(ToolFiles))]
    public void A_tools_declared_arguments_are_exactly_the_ones_its_code_reads(string file)
    {
        var text = File.ReadAllText(Path.Combine(Repo.SourceDir("Tools"), file));
        var read = Names(Reads, text).Except(["verb"]).ToList();
        var declared = Names(Declares, text).ToList();
        if (text.Contains("LogMatcher")) declared.AddRange(LogMatcher.Spec.Select(a => a.Name));    // shared by reference
        if (text.Contains("DockerTool.Resolve(")) read.AddRange(["target", "service", "project"]);  // read by the shared container lookup

        Assert.True(read.Except(declared).Count() == 0, $"{file} reads arguments its Spec does not declare: {string.Join(", ", read.Except(declared))}");
        Assert.True(declared.Except(read.Concat(LogMatcher.Spec.Select(a => a.Name))).Count() == 0,
            $"{file} declares arguments its code never reads: {string.Join(", ", declared.Except(read.Concat(LogMatcher.Spec.Select(a => a.Name))))}");
    }

    [Fact]
    public void LogMatcher_declares_exactly_the_arguments_it_reads()
    {
        var text = File.ReadAllText(Path.Combine(Repo.SourceDir("Tools"), "Common.cs"));
        Assert.Equal(Names(Reads, text), LogMatcher.Spec.Select(a => a.Name).Order().ToArray());
    }

    [Fact]
    public void Every_tool_has_unique_verbs_and_unique_arguments_per_verb()
    {
        foreach (var t in Registry.Default().All)
        {
            Assert.Equal(t.Spec.Count, t.Spec.Select(v => v.Verb).Distinct().Count());
            foreach (var v in t.Spec) Assert.True(v.Args.Select(a => a.Name).Distinct().Count() == v.Args.Count, $"{t.Name}/{v.Verb} repeats an argument");
        }
    }

    [Fact]
    public async Task Every_shipped_workflow_resolves_in_a_dry_run()
    {
        var cfg = ConfigLoader.LoadConfig(Repo.ShippedConfig);
        var state = ConfigLoader.LoadState(Repo.ShippedConfig, null, []);
        Assert.NotEmpty(cfg.Workflows);

        foreach (var (name, wf) in cfg.Workflows)      // Resolve throws for an unknown tool, arg set or verb
            await new WorkflowRunner(Registry.Default(), cfg, _ => { }).RunAsync(wf.Steps, state, new LocalHost(), dryRun: true, CancellationToken.None);
    }

    [Fact]
    public void Literal_verbs_in_shipped_args_exist_and_their_arguments_belong_to_that_verb()
    {
        var cfg = ConfigLoader.LoadConfig(Repo.ShippedConfig);
        var tools = Registry.Default();
        foreach (var (toolName, sets) in cfg.Args)
        {
            Assert.True(tools.TryGet(toolName, out var tool), $"args.json names unknown tool '{toolName}'");
            foreach (var (intent, set) in sets)
            {
                var verb = ToolContext.NodeStr(set["verb"])!;
                if (verb.Contains("${")) continue;
                var spec = tool.Spec.SingleOrDefault(v => v.Verb == verb);
                Assert.True(spec is not null, $"{toolName}/{intent}: no verb '{verb}'");
                var unknown = set.Select(p => p.Key).Except(spec!.Args.Select(a => a.Name)).Except(["verb"]).ToList();
                Assert.True(unknown.Count == 0, $"{toolName}/{intent}: '{string.Join(", ", unknown)}' is not an argument of {toolName} {verb}");
            }
        }
    }

    [Fact]
    public void Committed_schemas_match_what_the_generator_produces_now()
    {
        var cfg = ConfigLoader.LoadConfig(Repo.ShippedConfig);
        foreach (var (name, text) in Schemas.Generate(Registry.Default(), cfg))
        {
            var path = Path.Combine(Repo.ShippedConfig, "schemas", name);
            Assert.True(File.Exists(path), $"{name} is missing; run `toolrunner schema`");
            Assert.True(File.ReadAllText(path) == text, $"{name} is stale; run `toolrunner schema`");
        }
    }

    [Fact]
    public void Every_shipped_config_file_points_at_its_schema()
    {
        foreach (var (file, schema) in new[] { ("globals.json", "globals"), ("globals.staging.json", "globals"), ("args.json", "args"), ("workflows.json", "workflows") })
            Assert.Contains($"\"$schema\": \"schemas/{schema}.schema.json\"", File.ReadAllText(Path.Combine(Repo.ShippedConfig, file)));
    }
}
