using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// The plan cycle: OPTIONS go out in the spool, the assistant's PLAN comes back, is checked, and runs.
[Collection("sandbox")]
public class PlanTests
{
    private const string Reply = """
        1. ASSESSMENT
        Nothing has been run yet. The gateway publishes port 8080.

        2. PLAN
        ```json
        {
          "intent": "see whether gateway is running and what it logs",
          "vars": { "since": "1h" },
          "steps": [
            { "run": "compose/ps" },
            { "workflow": "digest", "params": { "service": "gateway" } },
            { "run": "docker", "with": { "verb": "logs", "service": "gateway", "tail": 5 } }
          ],
          "expect": "a running gateway container and no error lines"
        }
        ```

        3. WHY
        ...
        """;

    private static (ToolRegistry, ConfigSet, RunState) World(Sandbox sb) => (Registry.Default(), ConfigLoader.LoadConfig(sb.ConfigDir), sb.NewState());

    [Fact]
    public void Options_list_workflows_saved_commands_and_verbs_marked_reads_or_changes()
    {
        using var sb = new Sandbox();
        var (tools, cfg, state) = World(sb);
        var text = Planning.Options(tools, cfg, state);

        Assert.Matches(@"\[CHANGES\] suppress   params: service", text);
        Assert.Matches(@"\[reads\]   digest   params: service", text);
        Assert.Matches(@"\[reads\]   compose/ps   = compose ps   needs: -", text);
        Assert.Matches(@"\[CHANGES\] compose/up-service   = compose up   needs: service", text);
        Assert.Matches(@"\[reads\]   docker logs ", text);
        Assert.Contains("args: target, service, project, contains", text);
        Assert.Matches(@"\[reads\]   dotnet ExtractText", text);                       // plugin commands are options too
    }

    [Fact]
    public async Task A_reply_is_read_checked_and_run()
    {
        using var sb = new Sandbox();
        var (tools, cfg, state) = World(sb);
        Assert.True((await sb.Run("unsuppress", "x")).Ok);                              // something to look at: the stack is up

        var plan = Planning.Parse(Reply, tools, cfg, state);

        Assert.Empty(plan.Problems);
        Assert.Equal(["compose/ps", "docker/service-digest", "docker"], plan.Steps.Select(s => s.Run));     // the workflow is expanded
        Assert.Equal("gateway", plan.Vars["service"]);
        Assert.False(plan.Changes);
        Assert.StartsWith("a running gateway", plan.Expect);

        foreach (var (k, v) in plan.Vars) state.Vars[k] = v;
        var log = new List<string>();
        var ok = await new WorkflowRunner(tools, cfg, log.Add).RunAsync(plan.Steps, state, new LocalHost(), false, CancellationToken.None, readOnly: true);
        Assert.True(ok, string.Join("\n", log));
    }

    [Fact]
    public void Everything_that_does_not_exist_or_fit_is_reported_and_changes_are_flagged()
    {
        using var sb = new Sandbox();
        var (tools, cfg, state) = World(sb);
        var plan = Planning.Parse("""
            Here you go: { "steps": [
              { "workflow": "reboot-everything" },
              { "run": "compose/nope" },
              { "run": "docker", "with": { "verb": "exec", "service": "gateway" } },
              { "run": "docker", "with": { "verb": "logs", "service": "gateway", "colour": true } },
              { "run": "docker", "with": { "verb": "history" } },
              { "run": "compose/up-service" }
            ] }
            """, tools, cfg, state);

        Assert.Contains(plan.Problems, p => p.Contains("no workflow 'reboot-everything'"));
        Assert.Contains(plan.Problems, p => p.Contains("no saved command 'compose/nope'"));
        Assert.Contains(plan.Problems, p => p.Contains("docker has no verb 'exec'"));
        Assert.Contains(plan.Problems, p => p.Contains("no argument 'colour'"));
        Assert.Contains(plan.Problems, p => p.Contains("docker history needs 'image'"));
        Assert.Contains(plan.Problems, p => p.Contains("needs a value for 'service'"));
        Assert.True(plan.Changes);
    }

    [Fact]
    public void A_reply_without_a_plan_or_with_no_steps_is_handled()
    {
        using var sb = new Sandbox();
        var (tools, cfg, state) = World(sb);
        Assert.Contains("no plan found", Planning.Parse("I think you should restart it.", tools, cfg, state).Problems[0]);
        var none = Planning.Parse("PLAN { “intent”: “done”, \"steps\": [] }", tools, cfg, state);      // smart quotes from a chat window
        Assert.Empty(none.Problems);
        Assert.Empty(none.Steps);
        Assert.Equal("done", none.Intent);
    }

    [Fact]
    public void The_plan_spool_carries_intent_options_and_the_plan_answer_format()
    {
        using var sb = new Sandbox();
        var (tools, cfg, state) = World(sb);
        foreach (var f in new[] { "spool.plan.header.txt", "spool.plan.footer.txt" }) File.Copy(Path.Combine(Repo.ShippedConfig, f), Path.Combine(sb.ConfigDir, f));

        var text = Spool.Build(sb.ConfigDir, state, [], new SpoolOptions(0, [], false, false, "find out why gateway restarts", "plan"),
            extra: [Planning.OptionsSection(tools, cfg, state)]);

        Assert.Contains("##### MY INTENT\nfind out why gateway restarts", text);
        Assert.Contains("##### OPTIONS (everything toolrunner can do", text);
        Assert.Contains("STATE    what the sections below show", text);
        Assert.Contains("\"steps\": [", text);
        Assert.DoesNotContain("MY QUESTION", text);
        // the example in the answer format is itself a valid plan
        Assert.Empty(Planning.Parse(File.ReadAllText(Path.Combine(Repo.ShippedConfig, "spool.plan.footer.txt")), tools, cfg, state).Problems);
    }
}
