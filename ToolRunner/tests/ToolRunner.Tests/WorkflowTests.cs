using System.Text.Json.Nodes;

namespace ToolRunner.Tests;

/// The shipped workflows run end to end against a sandbox, including the ways they are meant to refuse.
[Collection("sandbox")]
public class WorkflowTests
{
    [Fact]
    public async Task Debug_points_the_consumer_at_the_debug_session_and_never_edits_the_original()
    {
        using var sb = new Sandbox();
        var before = File.ReadAllText(sb.Env);
        Assert.True((await sb.Stamp()).Ok);

        var r = await sb.Run("debug", "pluginhost");

        Assert.True(r.Ok, r.Text);
        var generated = File.ReadAllText(sb.DebugEnv);
        Assert.StartsWith("# GENERATED", generated);
        Assert.Contains("PLUGINHOST_HOST=host.docker.internal", generated);
        Assert.Contains("PLUGINHOST_PORT=5005", generated);
        Assert.Equal(before, File.ReadAllText(sb.Env));
        Assert.Equal("1", r.State.Produced["LOG_MATCHES"]);                 // the consumer logged the connection
        Assert.Contains("--no-deps", sb.DockerCalls);                       // the stopped service is not dragged back up
    }

    [Fact]
    public async Task Restore_removes_the_generated_files_and_starts_both_services_from_the_originals()
    {
        using var sb = new Sandbox();
        await sb.Stamp();
        Assert.True((await sb.Run("debug", "pluginhost")).Ok);
        Assert.True(File.Exists(sb.DebugEnv));

        var r = await sb.Run("restore", "pluginhost");

        Assert.True(r.Ok, r.Text);
        Assert.False(File.Exists(sb.DebugEnv));
        var lastUp = sb.DockerCalls.Split('\n').Last(l => l.Contains(" up "));
        Assert.Contains("--env-file " + sb.Env.Replace('\\', '/'), lastUp);        // the original env file, not the debug copy
    }

    [Fact]
    public async Task Debug_refuses_a_dirty_working_tree_before_touching_anything()
    {
        using var sb = new Sandbox();
        await sb.Stamp();
        File.AppendAllText(Path.Combine(sb.App, "docker-compose.yml"), "# uncommitted\n");

        var r = await sb.Run("debug", "pluginhost");

        Assert.False(r.Ok);
        Assert.Contains("uncommitted change", r.Text);
        Assert.False(File.Exists(sb.DebugEnv));
    }

    [Fact]
    public async Task Debug_fails_when_the_image_was_never_stamped()
    {
        using var sb = new Sandbox();
        var r = await sb.Run("debug", "pluginhost");

        Assert.False(r.Ok);
        Assert.Contains("not found on this host", r.Text);
        Assert.False(File.Exists(sb.DebugEnv));
    }

    [Fact]
    public async Task Debug_fails_when_the_source_has_moved_on_since_the_image_was_built()
    {
        using var sb = new Sandbox();
        await sb.Stamp();
        File.AppendAllText(Path.Combine(sb.Plugin, "main.py"), "print('changed')\n");
        Sandbox.Commit(sb.Plugin, "change");

        var r = await sb.Run("debug", "pluginhost");

        Assert.False(r.Ok);
        Assert.Contains("differs from image commit", r.Text);
    }

    [Fact]
    public async Task Debug_copes_with_an_env_file_that_mixes_line_endings()      // regression: derive used to find 0 lines
    {
        using var sb = new Sandbox();
        File.WriteAllText(sb.Env, "PLUGINHOST_HOST=pluginhost\nPLUGINHOST_PORT=5000\r\nLOG_LEVEL=info\r\n");
        await sb.Stamp();

        var r = await sb.Run("debug", "pluginhost");

        Assert.True(r.Ok, r.Text);
        Assert.Contains("PLUGINHOST_PORT=5005", File.ReadAllText(sb.DebugEnv));
    }

    [Fact]
    public async Task Isolate_is_stopped_by_validation_because_gateway_depends_on_the_commented_out_service()
    {
        using var sb = new Sandbox();      // documented gotcha: commenting out a service breaks depends_on pointing at it
        var r = await sb.Run("isolate", "pluginhost");

        Assert.False(r.Ok);
        Assert.Contains("undefined service", r.Text);
        Assert.DoesNotContain(" stop pluginhost", sb.DockerCalls);              // nothing was stopped before validation failed
    }

    [Fact]
    public async Task A_workflow_with_an_unknown_arg_set_is_rejected_before_any_step_runs()
    {
        using var sb = new Sandbox();
        var cfg = ToolRunner.Core.ConfigLoader.LoadConfig(sb.ConfigDir);
        var steps = new[]
        {
            new ToolRunner.Core.StepDef { Run = "file/remove-derived" },
            new ToolRunner.Core.StepDef { Run = "git/no-such-set" }
        };
        var runner = new ToolRunner.Core.WorkflowRunner(ToolRunner.Tools.Registry.Default(), cfg, _ => { });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(steps, sb.NewState(), new ToolRunner.Hosts.LocalHost(), false, CancellationToken.None));

        Assert.Contains("git/no-such-set", ex.Message);
        Assert.DoesNotContain("docker", sb.DockerCalls);
    }
}
