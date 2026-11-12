using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

[Collection("sandbox")]
public class ShellToolTests
{
    private static async Task<Outcome> Run(Sandbox sb, params JsonObject[] steps)
    {
        var log = new List<string>();
        var state = sb.NewState();
        var ok = await new WorkflowRunner(Registry.Default(), ConfigLoader.LoadConfig(sb.ConfigDir), log.Add)
            .RunAsync(steps.Select(s => new StepDef { Run = "shell", With = s }).ToList(), state, new LocalHost(), false, CancellationToken.None);
        return new Outcome(ok, log, state);
    }

    [Fact]
    public async Task Variables_go_into_the_command_and_its_output_comes_back_as_a_variable()
    {
        using var sb = new Sandbox();
        var r = await Run(sb,
            new() { ["verb"] = "query", ["command"] = "echo root is ${APP_ROOT}", ["into"] = "FIRST" },
            new() { ["verb"] = "query", ["command"] = "echo again: ${FIRST}" });

        Assert.True(r.Ok, r.Text);
        Assert.Equal("root is " + sb.App.Replace('\\', '/'), r.State.Vars["FIRST"]);
        Assert.StartsWith("again: root is ", r.State.Vars["SHELL_OUT"]);          // the next step read what the first produced
        Assert.Equal("0", r.State.Vars["SHELL_EXIT"]);
    }

    [Fact]
    public async Task Name_value_lines_json_and_named_groups_become_variables()
    {
        using var sb = new Sandbox();
        var r = await Run(sb,
            new() { ["verb"] = "query", ["command"] = "echo noise\necho VERSION=2.4.1\necho BUILD=77", ["capture"] = "lines", ["prefix"] = "APP_" },
            new() { ["verb"] = "query", ["shell"] = "powershell", ["command"] = "'{\"branch\":\"release/2.4\",\"dirty\":false}'", ["capture"] = "json" },
            new() { ["verb"] = "query", ["command"] = "echo image acme/orders-api:2.4.1-alpine3.20", ["regex"] = @":(?<TAG>(?<NUMBER>\d+\.\d+\.\d+)\S*)" });

        Assert.True(r.Ok, r.Text);
        Assert.Equal(("2.4.1", "77"), (r.State.Vars["APP_VERSION"], r.State.Vars["APP_BUILD"]));
        Assert.Equal(("release/2.4", "false"), (r.State.Vars["branch"], r.State.Vars["dirty"]));
        Assert.Equal(("2.4.1-alpine3.20", "2.4.1"), (r.State.Vars["TAG"], r.State.Vars["NUMBER"]));
    }

    [Fact]
    public async Task Quotes_and_pipes_survive_and_a_wrong_exit_code_fails_the_step()
    {
        using var sb = new Sandbox();
        var ok = await Run(sb, new JsonObject { ["verb"] = "query", ["command"] = "echo \"a b\" & echo c | findstr c" });
        Assert.True(ok.Ok, ok.Text);
        Assert.Equal("\"a b\" \nc", ok.State.Vars["SHELL_OUT"]);

        var bad = await Run(sb, new JsonObject { ["verb"] = "query", ["command"] = "echo oops 1>&2 & exit /b 3" });
        Assert.False(bad.Ok);
        Assert.Contains("cmd exited 3 (expected 0): oops", bad.Text);

        var expected = await Run(sb, new JsonObject { ["verb"] = "query", ["command"] = "exit /b 3", ["expectExit"] = 3 });
        Assert.True(expected.Ok, expected.Text);
    }

    [Fact]
    public async Task CONTAINER_ENGINE_replaces_docker_wherever_a_tool_runs_it()
    {
        using var sb = new Sandbox();
        var state = sb.NewState();
        Assert.Equal("docker", ((LocalHost)ConfigLoader.CreateHost(state, null)).Engine);
        state.Vars["CONTAINER_ENGINE"] = "git";                       // any other program on PATH proves the substitution
        var host = ConfigLoader.CreateHost(state, null);
        var r = await host.ExecAsync("docker", ["--version"], null, null, CancellationToken.None);
        Assert.StartsWith("git version", r.Stdout);
        Assert.Contains("\"adapter\": \"podman.exe\"", DebugImages.LaunchJson("c", null, null, null, engine: "podman"));
    }

    [Fact]
    public async Task Run_is_blocked_in_read_only_mode_and_query_is_not()
    {
        using var sb = new Sandbox();
        var cfg = ConfigLoader.LoadConfig(sb.ConfigDir);
        async Task<bool> Go(string verb) => await new WorkflowRunner(Registry.Default(), cfg, _ => { }).RunAsync(
            [new StepDef { Run = "shell", With = new() { ["verb"] = verb, ["command"] = "echo hi" } }], sb.NewState(), new LocalHost(), false, CancellationToken.None, readOnly: true);
        Assert.False(await Go("run"));
        Assert.True(await Go("query"));
    }
}
