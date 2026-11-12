using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// Commands from the plugins folder (the SamplePlugin project copies itself there when it builds).
[Collection("sandbox")]
public class DotnetPluginTests
{
    private static async Task<(ToolResult Result, List<string> Log)> Run(JsonObject args)
    {
        var log = new List<string>();
        var r = await new DotnetTool().RunAsync(new ToolContext { State = new RunState(), Args = args, Host = new LocalHost(), Log = log.Add });
        return (r, log);
    }

    [Fact]
    public void Classes_implementing_IDotnetTool_become_verbs_with_their_Args_in_order()
    {
        var spec = new DotnetTool().Spec;
        var extract = spec.Single(v => v.Verb == "ExtractText");
        Assert.Equal(["Text", "Pattern"], extract.Args.Select(a => a.Name));
        Assert.All(extract.Args, a => Assert.True(a.Required));
        Assert.False(extract.Mutates);                                             // [ReadOnly]
        Assert.Contains("Finds the first match", extract.Doc);
        Assert.True(spec.Single(v => v.Verb == "UpperCaseFile").Mutates);          // unmarked: treated as changing things
    }

    [Fact]
    public async Task What_a_command_returns_becomes_variables()
    {
        var (r, _) = await Run(new() { ["verb"] = "ExtractText", ["Text"] = "acme/orders-api:2.4.1-alpine3.20", ["Pattern"] = @"\d+\.\d+\.\d+" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("2.4.1", r.Outputs!["EXTRACTED"]);
        Assert.Equal("true", r.Outputs["EXTRACTED_FOUND"]);
    }

    [Fact]
    public async Task An_exception_in_a_command_fails_the_step_with_its_message()
    {
        var (r, _) = await Run(new() { ["verb"] = "UpperCaseFile", ["InputFilePath"] = "Z:/no/such/file.txt", ["OutputFilePath"] = "Z:/out.txt" });
        Assert.False(r.Ok);
        Assert.Contains("UpperCaseFile threw", r.Message);
    }

    [Fact]
    public async Task A_command_runs_as_a_workflow_step_and_feeds_the_next_one_and_read_only_mode_blocks_the_writers()
    {
        using var sb = new Sandbox();
        var input = Path.Combine(sb.Root, "in.txt");
        File.WriteAllText(input, "hello");
        var cfg = ConfigLoader.LoadConfig(sb.ConfigDir);
        StepDef[] steps =
        [
            new() { Run = "dotnet", With = new() { ["verb"] = "ExtractText", ["Text"] = input, ["Pattern"] = @"[^\\/]+$" } },
            new() { Run = "dotnet", With = new() { ["verb"] = "UpperCaseFile", ["InputFilePath"] = input, ["OutputFilePath"] = sb.Root + "/${EXTRACTED}.up" } }
        ];

        var log = new List<string>();
        var blocked = await new WorkflowRunner(Registry.Default(), cfg, log.Add).RunAsync(steps, sb.NewState(), new LocalHost(), false, CancellationToken.None, readOnly: true);
        Assert.False(blocked);
        Assert.False(File.Exists(Path.Combine(sb.Root, "in.txt.up")));

        var ok = await new WorkflowRunner(Registry.Default(), cfg, log.Add).RunAsync(steps, sb.NewState(), new LocalHost(), false, CancellationToken.None);
        Assert.True(ok, string.Join("\n", log));
        Assert.Equal("HELLO", File.ReadAllText(Path.Combine(sb.Root, "in.txt.up")));
    }

    [Fact]
    public async Task List_reports_what_was_found()
    {
        var (r, log) = await Run(new() { ["verb"] = "list" });
        Assert.True(r.Ok);
        Assert.Contains(log, l => l.StartsWith("ExtractText(Text, Pattern)") && l.Contains("reads"));
    }
}
