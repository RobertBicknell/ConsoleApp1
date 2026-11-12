using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// Suppressing a service for local debugging, and undoing it.
[Collection("sandbox")]
public class SuppressTests
{
    [Fact]
    public async Task Suppress_removes_the_service_relaxes_dependants_switches_host_and_port_and_validates()
    {
        using var sb = new Sandbox();
        var composeBefore = File.ReadAllText(Path.Combine(sb.App, "docker-compose.yml"));

        var r = await sb.Run("suppress", "pluginhost");

        Assert.True(r.Ok, r.Text);
        var derived = File.ReadAllText(Path.Combine(sb.App, ".toolrunner", "docker-compose.debug.yml"));
        Assert.Contains("#  pluginhost:", derived);
        Assert.Contains("#    depends_on:", derived);                       // gateway no longer waits for it
        Assert.Contains("  gateway:", derived);
        Assert.Contains("PLUGINHOST_HOST=host.docker.internal", File.ReadAllText(sb.DebugEnv));
        Assert.Equal(composeBefore, File.ReadAllText(Path.Combine(sb.App, "docker-compose.yml")));
        var up = sb.DockerCalls.Split('\n').Last(l => l.Contains(" up "));
        Assert.Contains("docker-compose.debug.yml", up);                    // the rest of the stack runs from the copies
        Assert.Contains("debug.env", up);
    }

    [Fact]
    public async Task Unsuppress_deletes_the_copies_and_starts_everything_from_the_originals()
    {
        using var sb = new Sandbox();
        Assert.True((await sb.Run("suppress", "pluginhost")).Ok);

        var r = await sb.Run("unsuppress", "pluginhost");

        Assert.True(r.Ok, r.Text);
        Assert.False(File.Exists(sb.DebugEnv));
        Assert.False(File.Exists(Path.Combine(sb.App, ".toolrunner", "docker-compose.debug.yml")));
        var up = sb.DockerCalls.Split('\n').Last(l => l.Contains(" up "));
        Assert.DoesNotContain("debug", up);
        Assert.DoesNotContain("--no-deps", up);                             // whole stack, dependencies included
    }
}

public class DropDependsOnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-deps-" + Guid.NewGuid().ToString("N")[..8]);
    public DropDependsOnTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private async Task<(ToolResult Result, string[] Lines)> Drop(string yaml, string service, string? extra = null)
    {
        File.WriteAllText(Path.Combine(_dir, "in.yml"), yaml);
        var args = new JsonObject
        {
            ["verb"] = "derive", ["source"] = Path.Combine(_dir, "in.yml"), ["target"] = Path.Combine(_dir, "out.yml"),
            ["edits"] = JsonNode.Parse($$"""[{"dropDependsOn":"{{service}}"{{extra}}}]""")
        };
        var r = await new FileTool().RunAsync(new ToolContext { State = new RunState(), Args = args, Host = new LocalHost(), Log = _ => { } });
        return (r, r.Ok ? File.ReadAllLines(Path.Combine(_dir, "out.yml"))[1..] : []);
    }

    [Fact]
    public async Task List_form_keeps_other_dependencies_and_the_key()
    {
        var (r, lines) = await Drop("services:\n  a:\n    depends_on:\n      - db\n      - cache\n  db:\n    image: x\n", "db");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(["services:", "  a:", "    depends_on:", "#      - db", "      - cache", "  db:", "    image: x"], lines[..7]);
    }

    [Fact]
    public async Task The_key_itself_is_commented_when_nothing_is_left_under_it()
    {
        var (_, lines) = await Drop("services:\n  a:\n    depends_on:\n      - db\n    image: y\n", "db");

        Assert.Equal(["#    depends_on:", "#      - db", "    image: y"], lines[2..5]);
    }

    [Fact]
    public async Task Map_form_takes_the_condition_lines_with_it()
    {
        var (_, lines) = await Drop("services:\n  a:\n    depends_on:\n      db:\n        condition: service_healthy\n      cache:\n        condition: service_started\n", "db");

        Assert.Equal(["    depends_on:", "#      db:", "#        condition: service_healthy", "      cache:", "        condition: service_started"], lines[2..7]);
    }

    [Fact]
    public async Task Flow_form_is_rewritten_in_place()
    {
        var (_, lines) = await Drop("services:\n  a:\n    depends_on: [db, cache]\n  b:\n    depends_on: [db]\n", "db");

        Assert.Equal("    depends_on: [cache]", lines[2]);
        Assert.Equal("#    depends_on: [db]", lines[4]);
    }

    [Fact]
    public async Task No_references_is_fine_unless_a_count_is_expected()
    {
        const string yaml = "services:\n  a:\n    image: x\n";
        Assert.True((await Drop(yaml, "db")).Result.Ok);
        Assert.False((await Drop(yaml, "db", ",\"expect\":1")).Result.Ok);
    }
}

public class PluginCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-plug-" + Guid.NewGuid().ToString("N")[..8]).Replace('\\', '/');
    public PluginCheckTests() => Directory.CreateDirectory(_dir + "/plugins");
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Names_are_detected_under_plugin_like_keys_and_from_dll_strings_but_not_from_ordinary_settings()
    {
        var names = PluginCheck.AssemblyNames("""
            {
              // comments are allowed in appsettings
              "Logging": { "LogLevel": { "Default": "Information" } },
              "ConnectionStrings": { "Main": "Server=db;Database=app" },
              "Plugins": {
                "Path": "/app/plugins",
                "Assemblies": [ "Acme.Billing", "Acme.Audit.dll" ],
                "Items": [ { "Name": "Tax", "Assembly": "AcmeTax", "Type": "Acme.Reports.Plugin, Acme.Reports" } ]
              },
              "Startup": { "Hook": "lib/Acme.Hook.dll" }
            }
            """).Select(n => n.Name).ToArray();

        Assert.Equal(["Acme.Billing.dll", "Acme.Audit.dll", "AcmeTax.dll", "Acme.Reports.dll", "Acme.Hook.dll"], names);
    }

    [Fact]
    public void An_explicit_key_takes_every_string_under_it_and_nothing_else()
    {
        var names = PluginCheck.AssemblyNames("""{ "Modules": { "Load": [ "one", "two" ] }, "Plugins": [ "Acme.Other" ] }""", "Modules:Load");
        Assert.Equal(["one.dll", "two.dll"], names.Select(n => n.Name).ToArray());
    }

    [Fact]
    public async Task Check_reports_found_missing_and_unlisted_binaries()
    {
        File.WriteAllText(_dir + "/appsettings.json", """{ "Plugins": { "Assemblies": [ "Acme.A", "Acme.B" ] } }""");
        File.WriteAllText(_dir + "/plugins/acme.a.dll", "");          // name match is case-insensitive
        File.WriteAllText(_dir + "/plugins/Acme.Extra.dll", "");

        var r = await PluginCheck.CheckAsync(new LocalHost(), _dir + "/appsettings.json", "svc", [_dir], null, CancellationToken.None);

        Assert.Equal(1, r.Missing);
        Assert.EndsWith("/plugins/acme.a.dll", r.Items.Single(i => i.Name == "Acme.A.dll").FoundAt);
        Assert.Null(r.Items.Single(i => i.Name == "Acme.B.dll").FoundAt);
        Assert.EndsWith("Acme.Extra.dll", Assert.Single(r.Unlisted));
    }

    [Fact]
    public async Task The_scan_ties_an_appsettings_file_to_the_service_that_bind_mounts_it()
    {
        File.WriteAllText(_dir + "/docker-compose.yml", "services:\n  host:\n    image: x\n    volumes:\n      - ./data:/app/data\n  other:\n    image: y\n");
        Directory.CreateDirectory(_dir + "/data/plugins");
        File.WriteAllText(_dir + "/data/appsettings.json", """{ "Plugins": { "Assemblies": [ "Acme.A" ] } }""");
        File.WriteAllText(_dir + "/data/plugins/Acme.A.dll", "");

        var stack = (await StackScanner.ScanDirectoryAsync(new LocalHost(), _dir, 4, CancellationToken.None)).Stacks.Single();

        var report = Assert.Single(stack.Plugins);
        Assert.Equal("host", report.Service);
        Assert.Equal(0, report.Missing);
    }
}

public class CatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-cat-" + Guid.NewGuid().ToString("N")[..8]).Replace('\\', '/');
    public CatalogTests()
    {
        Directory.CreateDirectory(_dir + "/app");
        File.WriteAllText(_dir + "/app/.env", "API_HOST=api\nTAG=2.1\n");
        File.WriteAllText(_dir + "/app/docker-compose.yml",
            "services:\n  api:\n    image: acme/api:${TAG}\n    container_name: the-api\n    depends_on:\n      db:\n        condition: service_healthy\n  db:\n    image: postgres:16\n  built:\n    build: .\n    depends_on: [api, db]\n");
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private async Task<Catalog> Build() =>
        new(_dir, await StackScanner.ScanDirectoryAsync(new LocalHost(), _dir, 4, CancellationToken.None), ["local/only:1"], ["the-api"], DateTimeOffset.Now);

    [Fact]
    public async Task Services_carry_image_container_name_and_dependencies_in_every_form()
    {
        var s = (await Build()).Scan.Stacks.Single().Services.ToDictionary(x => x.Name);

        Assert.Equal(("acme/api:2.1", "the-api"), (s["api"].Image, s["api"].ContainerName));       // ${TAG} came from .env
        Assert.Equal(["db"], s["api"].DependsOn);
        Assert.Equal(["api", "db"], s["built"].DependsOn);
        Assert.True(s["built"].HasBuild);
        Assert.Null(s["built"].Image);
    }

    [Fact]
    public async Task Suggestions_follow_the_meaning_of_the_argument_name()
    {
        var c = await Build();
        var state = new RunState();
        state.Vars["APP_ROOT"] = _dir + "/app"; state.Vars["ENV_FILE"] = "${APP_ROOT}/.env"; state.Vars["services.api.image"] = "x";

        Assert.Equal(["api", "built", "db"], Suggest.For(c, state, "service").Take(3).ToArray());
        Assert.Contains("postgres:16", Suggest.For(c, state, "image"));
        Assert.Contains("local/only:1", Suggest.For(c, state, "image"));                // from docker, not from compose
        Assert.Contains("API_HOST", Suggest.For(c, state, "set"));
        Assert.Contains("${ENV_FILE}", Suggest.For(c, state, "envFile"));
        Assert.Contains(_dir + "/app/docker-compose.yml", Suggest.For(c, state, "files"));
        Assert.Contains("the-api", Suggest.For(c, state, "target", new Arg("target", "string", "Container name or id.")));
        Assert.Contains("${APP_ROOT}", Suggest.For(c, state, "anythingElse"));           // variables are always on offer
    }

    [Fact]
    public void Without_a_catalog_suggestions_still_come_from_variables_and_declared_values()
    {
        var state = new RunState();
        state.Vars["services.web.image"] = "x";

        Assert.Contains("web", Suggest.For(null, state, "service"));
        Assert.Equal(["present", "absent"], Suggest.For(null, state, "expect", A.Enum("expect", "", "present", "absent")).Take(2).ToArray());
        Assert.Equal(["recent-one"], Suggest.For(null, state, "x", recent: ["recent-one"]).Take(1).ToArray());
    }
}

public class ConfigWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-write-" + Guid.NewGuid().ToString("N")[..8]);
    public ConfigWriterTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "globals.json"), """{ "PORT": "1", "services": { "web": { "port": "80" } } }""");
    }
    public void Dispose() => Directory.Delete(_dir, true);

    private Dictionary<string, string> Vars() => ConfigLoader.LoadState(_dir, null, []).Vars;

    [Fact]
    public void A_user_override_wins_and_removing_it_restores_the_original()
    {
        ConfigWriter.SetUserVar(_dir, "PORT", "2");
        ConfigWriter.SetUserVar(_dir, "services.web.port", "8080");
        Assert.Equal(("2", "8080"), (Vars()["PORT"], Vars()["services.web.port"]));
        Assert.Contains("\"PORT\": \"1\"", File.ReadAllText(Path.Combine(_dir, "globals.json")));     // the shared file is never written

        Assert.True(ConfigWriter.RemoveUserVar(_dir, "services.web.port"));
        Assert.True(ConfigWriter.RemoveUserVar(_dir, "PORT"));
        Assert.Equal(("1", "80"), (Vars()["PORT"], Vars()["services.web.port"]));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_dir, "globals.user.json")).Trim());          // empty parents are pruned
        Assert.False(ConfigWriter.RemoveUserVar(_dir, "PORT"));
    }

    [Fact]
    public void Layers_say_where_each_value_comes_from()
    {
        ConfigWriter.SetUserVar(_dir, "PORT", "2");
        var layers = ConfigLoader.LoadLayers(_dir, null);

        Assert.Equal(["globals", "user"], layers.Select(l => l.Layer).ToArray());
        Assert.Equal("2", layers[1].Vars["PORT"]);
        Assert.False(layers[1].Vars.ContainsKey("services.web.port"));
    }

    [Fact]
    public void A_saved_workflow_and_arg_set_load_back_and_run_through_validation()
    {
        ConfigWriter.SaveArgSet(_dir, "compose", "stop-it", new JsonObject { ["verb"] = "stop", ["service"] = "${service}" });
        ConfigWriter.SaveWorkflow(_dir, "halt", new WorkflowDef
        {
            Description = "stop one", Params = ["service"],
            Steps = [new StepDef { Run = "compose/stop-it" }, new StepDef { Run = "git", With = new JsonObject { ["verb"] = "head" }, ContinueOnError = true }]
        });

        var cfg = ConfigLoader.LoadConfig(_dir);

        var wf = cfg.Workflows["halt"];
        Assert.Equal(["service"], wf.Params);
        Assert.Equal(["compose/stop-it", "git"], wf.Steps.Select(s => s.Run).ToArray());
        Assert.True(wf.Steps[1].ContinueOnError);
        Assert.Equal("stop", ToolContext.NodeStr(cfg.Args["compose"]["stop-it"]["verb"]));
        Assert.True(ConfigWriter.DeleteWorkflow(_dir, "halt"));
        Assert.Empty(ConfigLoader.LoadConfig(_dir).Workflows);
    }

    [Fact]
    public void Names_that_would_break_the_command_line_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ConfigWriter.SaveWorkflow(_dir, "two words", new WorkflowDef()));
        Assert.Throws<ArgumentException>(() => ConfigWriter.SaveArgSet(_dir, "git", "a/b", new JsonObject()));
    }

    [Fact]
    public void Implied_params_are_the_undefined_plain_names_a_chain_uses()
    {
        var state = new RunState();
        state.Vars["APP_ROOT"] = "/x"; state.Vars["services.api.image"] = "i";
        var steps = new[]
        {
            new JsonObject { ["service"] = "${service}", ["path"] = "${APP_ROOT}", ["image"] = "${services.${service}.image}" },
            new JsonObject { ["since"] = "${STARTED_AT_UTC}", ["tail"] = "${lines:-50}", ["list"] = new JsonArray("${other}") }
        };

        Assert.Equal(["service", "other"], ConfigWriter.ImpliedParams(steps, state, ["STARTED_AT_UTC"]));
    }
}

public class LogLensTests
{
    [Theory]
    [InlineData("fail: App.Db[1] boom", "error")]
    [InlineData("2026-01-01T00:00:00Z [ERR] boom", "error")]
    [InlineData("System.InvalidOperationException: x", "error")]
    [InlineData("   at App.Run() in /src/a.cs:line 4", "error")]
    [InlineData("warn: App.Db[1] retrying", "warn")]
    [InlineData("info: App[0] started", "info")]
    [InlineData("dbug: App[0] detail", "debug")]
    [InlineData("plain text", "other")]
    public void Lines_are_classified_by_severity(string line, string level) => Assert.Equal(level, LogLens.Level(line));

    [Fact]
    public void Repeats_that_differ_only_in_numbers_ids_and_timestamps_collapse_and_errors_come_first()
    {
        var lines = Enumerable.Range(1, 50).Select(i => $"2026-01-01T00:00:{i % 60:00}Z info: Health[0] check {i} took {i * 3}ms id 0f8fad5b-d9cb-469f-a165-70867728950e")
            .Append("2026-01-01T00:01:00Z fail: Loader[7] Could not load 'Acme.Billing.dll'")
            .Append("2026-01-01T00:01:01Z warn: Db[1] Retry 2 of 5").Append("2026-01-01T00:01:02Z warn: Db[1] Retry 3 of 5");

        var d = LogLens.Digest(lines);

        Assert.Equal([("error", 1), ("warn", 2), ("info", 50)], d.Select(p => (p.Level, p.Count)).ToArray());
        Assert.Equal("info: Health[#] check # took #ms id <id>", d[2].Template);
        Assert.StartsWith("digest: 53 line(s) in 3 pattern(s): 1 error, 2 warn, 50 info", LogLens.Text(d).First());
    }
}
