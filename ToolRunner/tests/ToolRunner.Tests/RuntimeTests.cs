using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// Read-only mode, remarks, the runtime check, plugin publishing and git as a reference.
[Collection("sandbox")]
public class RuntimeSandboxTests
{
    private static async Task<RuntimeReport> Check(Sandbox sb)
    {
        var scan = await StackScanner.ScanDirectoryAsync(new LocalHost(), sb.App.Replace('\\', '/'), 3, CancellationToken.None);
        return await RuntimeCheck.RunAsync(new LocalHost(), scan, CancellationToken.None);
    }

    [Fact]
    public async Task Freshly_started_containers_match_the_configuration_and_drift_shows_after_an_edit()
    {
        using var sb = new Sandbox();
        Assert.True((await sb.RunStep("compose", new JsonObject { ["verb"] = "up" })).Ok);

        var before = await Check(sb);
        Assert.True(before.Problems == 0, RuntimeCheck.Text(before, sb.App));
        Assert.Contains(before.Findings, f => f is { Service: "gateway", Area: "port", Status: "ok" });
        Assert.Contains(before.Findings, f => f is { Service: "gateway", Area: "env", Item: "LOG_LEVEL", Status: "ok", Actual: "info" });

        // someone edits the files; nothing is restarted
        File.WriteAllText(sb.Env, "PLUGINHOST_HOST=pluginhost\nPLUGINHOST_PORT=5000\nLOG_LEVEL=debug\nGATEWAY_PORT=9000\n");
        var after = await Check(sb);

        Assert.Contains(after.Findings, f => f is { Service: "gateway", Area: "port", Status: "differs", Expected: "host port 9000", Actual: "host port 8080" });
        Assert.Contains(after.Findings, f => f is { Service: "gateway", Area: "env", Item: "LOG_LEVEL", Status: "differs", Expected: "debug", Actual: "info" });
        Assert.Contains(after.Findings, f => f is { Service: "pluginhost", Area: "image", Status: "ok" });
    }

    [Fact]
    public async Task A_stopped_container_and_a_missing_one_are_told_apart()
    {
        using var sb = new Sandbox();
        await sb.RunStep("compose", new JsonObject { ["verb"] = "up", ["service"] = "gateway" });
        await sb.RunStep("compose", new JsonObject { ["verb"] = "stop", ["service"] = "gateway" });

        var r = await Check(sb);

        Assert.Contains(r.Findings, f => f is { Service: "gateway", Area: "container", Status: "not-running", Actual: "exited" });
        Assert.Contains(r.Findings, f => f is { Service: "gateway", Area: "port", Status: "not-running" });
        Assert.Contains(r.Findings, f => f is { Service: "pluginhost", Area: "container", Status: "missing" });
    }

    [Fact]
    public async Task Read_only_mode_refuses_a_run_that_would_change_anything_before_any_step_runs()
    {
        using var sb = new Sandbox();
        var cfg = ConfigLoader.LoadConfig(sb.ConfigDir);
        var log = new List<string>();
        var runner = new WorkflowRunner(Registry.Default(), cfg, log.Add);
        var state = sb.NewState(); state.Vars["service"] = "pluginhost";

        var ok = await runner.RunAsync(cfg.Workflows["suppress"].Steps, state, new LocalHost(), false, CancellationToken.None, readOnly: true);

        Assert.False(ok);
        Assert.Contains(log, l => l.StartsWith("BLOCKED file/derive-compose-suppressed"));
        Assert.DoesNotContain(log, l => l.Contains("RAN"));                    // the read-only first step did not run either
        Assert.Equal("", sb.DockerCalls);
        Assert.False(File.Exists(sb.DebugEnv));
    }

    [Fact]
    public async Task Read_only_mode_allows_reading_and_dry_runs()
    {
        using var sb = new Sandbox();
        var cfg = ConfigLoader.LoadConfig(sb.ConfigDir);
        var runner = new WorkflowRunner(Registry.Default(), cfg, _ => { });
        var state = sb.NewState(); state.Vars["service"] = "pluginhost";
        StepDef[] reads = [new() { Run = "git", With = new JsonObject { ["verb"] = "status" } }, new() { Run = "compose/ps" }];

        Assert.True(await runner.RunAsync(reads, state, new LocalHost(), false, CancellationToken.None, readOnly: true));
        Assert.True(await runner.RunAsync(cfg.Workflows["suppress"].Steps, state, new LocalHost(), dryRun: true, CancellationToken.None, readOnly: true));
    }

    [Fact]
    public async Task A_real_run_logs_what_each_step_ran_with_and_tags_stderr()
    {
        using var sb = new Sandbox();
        var r = await sb.Run("isolate", "pluginhost");        // fails at validation; the fake docker reports that on stderr

        Assert.Contains(r.Log, l => l.StartsWith("    RAN {\"verb\":\"require-clean\""));
        Assert.Contains(r.Log, l => l.Contains("(stderr)") && l.Contains("undefined service"));
    }

    [Fact]
    public async Task Loaded_reads_the_logs_for_each_assembly()
    {
        using var sb = new Sandbox();
        await sb.RunStep("compose", new JsonObject { ["verb"] = "up" });
        Task<Outcome> Loaded(string names, bool allowSilent = false) => sb.RunStep("plugins", new JsonObject
        {
            ["verb"] = "loaded", ["service"] = "gateway", ["assemblies"] = JsonNode.Parse(names), ["allowSilent"] = allowSilent
        });

        var failed = await Loaded("""["Acme.Billing"]""");               // the fake gateway logs a load failure for it
        Assert.False(failed.Ok);
        Assert.Contains("Acme.Billing (error)", failed.Text);

        Assert.True((await Loaded("""["gateway.Health"]""")).Ok);          // mentioned, never in an error line
        Assert.Contains("(not mentioned)", (await Loaded("""["Acme.Nowhere"]""")).Text);
        Assert.True((await Loaded("""["Acme.Nowhere"]""", allowSilent: true)).Ok);
    }
}

public class RuntimeParsingTests
{
    private const string Inspect = """
        [{
          "Id": "0123456789abcdef0123", "Name": "/shop-web-1",
          "State": { "Status": "running", "Running": true },
          "Config": { "Image": "acme/web:2", "Env": ["A=1", "CONN=Host=db;Password=x=y", "PATH=/bin"],
                      "Labels": { "com.docker.compose.project": "shop", "com.docker.compose.service": "web" } },
          "NetworkSettings": { "Ports": { "80/tcp": [ { "HostIp": "0.0.0.0", "HostPort": "8080" }, { "HostIp": "::", "HostPort": "8080" } ], "9000/tcp": null } },
          "Mounts": [ { "Type": "bind", "Source": "/run/desktop/mnt/host/c/deploy/shop/config", "Destination": "/app/config", "RW": false },
                      { "Type": "volume", "Name": "shop_data", "Source": "/var/lib/docker/volumes/shop_data/_data", "Destination": "/data", "RW": true } ]
        },
        {
          "Id": "ffff456789abcdef0123", "Name": "/other-proxy-1",
          "State": { "Status": "running", "Running": true },
          "Config": { "Image": "nginx", "Env": [], "Labels": { "com.docker.compose.project": "other", "com.docker.compose.service": "proxy" } },
          "NetworkSettings": { "Ports": { "80/tcp": [ { "HostIp": "0.0.0.0", "HostPort": "9090" } ] } }, "Mounts": []
        }]
        """;

    [Fact]
    public void Inspect_output_is_read_into_ports_mounts_env_and_compose_labels()
    {
        var c = RuntimeCheck.Parse(Inspect)[0];

        Assert.Equal(("shop-web-1", "running", true, "acme/web:2", "shop", "web"), (c.Name, c.Status, c.Running, c.Image, c.Project, c.Service));
        Assert.Equal("Host=db;Password=x=y", c.Env["CONN"]);                        // only the first '=' separates
        Assert.Equal(new PortBinding("0.0.0.0", "8080", "80", "tcp"), Assert.Single(c.Ports));      // IPv6 twin and unpublished port dropped
        Assert.Equal([("bind", "/app/config", false), ("volume", "/data", true)], c.Mounts.Select(m => (m.Type, m.Destination, m.ReadWrite)).ToArray());
    }

    [Theory]
    [InlineData("/run/desktop/mnt/host/c/deploy/shop/config", "c:/deploy/shop/config")]
    [InlineData("/host_mnt/c/Deploy/Shop/config/", "c:/deploy/shop/config")]
    [InlineData(@"C:\deploy\shop\config", "c:/deploy/shop/config")]
    [InlineData("/c/deploy/shop/config", "c:/deploy/shop/config")]
    [InlineData("/srv/shop/config", "/srv/shop/config")]
    public void Bind_sources_compare_equal_however_docker_spells_the_windows_path(string reported, string normal) =>
        Assert.Equal(normal, RuntimeCheck.NormalisePath(reported));

    [Fact]
    public async Task Configured_mounts_ports_and_a_port_held_by_another_container_are_tallied()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolrunner-rt-" + Guid.NewGuid().ToString("N")[..8], "shop").Replace('\\', '/');
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(dir + "/docker-compose.yml",
                "services:\n  web:\n    image: acme/web:3\n    ports:\n      - 8080:80\n      - 9090:90\n    volumes:\n      - C:/deploy/shop/config:/app/config:ro\n      - data:/data\n      - ./extra:/extra\n    environment:\n      - A=1\n      - B=2\nvolumes:\n  data:\n");
            var stack = (await StackScanner.ScanDirectoryAsync(new LocalHost(), dir, 2, CancellationToken.None)).Stacks.Single();

            var f = RuntimeCheck.Check(stack, RuntimeCheck.Parse(Inspect)).ToDictionary(x => $"{x.Area} {x.Item}", x => x.Status);

            Assert.Equal("differs", f["image image"]);
            Assert.Equal("ok", f["port *:8080 -> 80/tcp"]);
            Assert.Equal("missing", f["port *:9090 -> 90/tcp"]);
            Assert.Equal("taken", f["port host port 9090/tcp"]);                  // ...because other-proxy-1 holds it
            Assert.Equal("ok", f["mount bind /app/config"]);                      // same folder, spelled docker-desktop style
            Assert.Equal("ok", f["mount volume /data"]);                          // shop_data is the project-prefixed name
            Assert.Equal("missing", f["mount bind /extra"]);
            Assert.Equal("ok", f["env A"]);
            Assert.Equal("missing", f["env B"]);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }
}

public class PublishAndGitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolrunner-pub-" + Guid.NewGuid().ToString("N")[..8]).Replace('\\', '/');
    public PublishAndGitTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private void Write(string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName($"{_root}/{rel}")!); File.WriteAllText($"{_root}/{rel}", text); }

    private Task<ToolResult> Publish(List<string> log, string? assemblies = null)
    {
        var args = new JsonObject
        {
            ["verb"] = "publish", ["project"] = _root + "/src/Acme.New", ["dir"] = _root + "/deploy/plugins", ["appsettings"] = _root + "/deploy/config/appsettings.json",
            ["build"] = false, ["output"] = _root + "/src/Acme.New/bin/Debug/net9.0"       // the build itself is dotnet's job; this tests what happens around it
        };
        if (assemblies is not null) args["assemblies"] = JsonNode.Parse(assemblies);
        return new PluginsTool().RunAsync(new ToolContext { State = new RunState(), Args = args, Host = new LocalHost(), Log = log.Add });
    }

    [Fact]
    public async Task Publish_copies_what_changed_lists_the_assembly_and_keeps_a_backup_then_is_idempotent()
    {
        Write("src/Acme.New/Acme.New.csproj", "<Project />");
        Write("src/Acme.New/bin/Debug/net9.0/Acme.New.dll", "new build");
        Write("src/Acme.New/bin/Debug/net9.0/Some.Dependency.dll", "dep");
        Write("deploy/plugins/Acme.New.dll", "old build");
        Write("deploy/plugins/Some.Dependency.dll", "dep");                                     // identical: not copied again
        Write("deploy/config/appsettings.json", """{ "Logging": { "Level": "Info" }, "Plugins": { "Assemblies": [ "Acme.Old" ] } }""");
        var log = new List<string>();

        var r = await Publish(log);

        Assert.True(r.Ok, r.Message + string.Join("\n", log));
        Assert.Equal("new build", File.ReadAllText(_root + "/deploy/plugins/Acme.New.dll"));
        Assert.Equal(("1", "Acme.New"), (r.Outputs!["PLUGINS_COPIED"], r.Outputs["PLUGINS_ADDED"]));
        var names = PluginCheck.AssemblyNames(File.ReadAllText(_root + "/deploy/config/appsettings.json"), "Plugins:Assemblies").Select(n => n.Name).ToArray();
        Assert.Equal(["Acme.Old.dll", "Acme.New.dll"], names);                                    // the dependency is not listed
        Assert.Contains("Acme.Old", File.ReadAllText(r.Outputs["PLUGIN_SETTINGS_BACKUP"]));       // the file as it was
        Assert.DoesNotContain("Acme.New", File.ReadAllText(r.Outputs["PLUGIN_SETTINGS_BACKUP"]));

        var again = await Publish(log);
        Assert.Equal(("0", ""), (again.Outputs!["PLUGINS_COPIED"], again.Outputs["PLUGINS_ADDED"]));
        Assert.False(again.Outputs.ContainsKey("PLUGIN_SETTINGS_BACKUP"));
    }

    [Fact]
    public async Task Publish_follows_the_files_naming_style_and_refuses_an_assembly_that_was_not_built()
    {
        Write("src/Acme.New/Acme.New.csproj", "<Project><PropertyGroup><AssemblyName>Acme.Renamed</AssemblyName></PropertyGroup></Project>");
        Write("src/Acme.New/bin/Debug/net9.0/Acme.Renamed.dll", "x");
        Write("deploy/config/appsettings.json", """{ "Plugins": { "Assemblies": [ "Acme.Old.dll" ] } }""");

        Assert.True((await Publish(new())).Ok);
        Assert.Contains("\"Acme.Renamed.dll\"", File.ReadAllText(_root + "/deploy/config/appsettings.json"));       // .dll kept, as the file had it

        var missing = await Publish(new(), """["Acme.NotBuilt"]""");
        Assert.False(missing.Ok);
        Assert.Contains("Acme.NotBuilt.dll is not in the build output", missing.Message);
    }

    [Fact]
    public async Task Git_facts_and_a_commit_as_the_reference()
    {
        var repo = _root + "/repo";
        Write("repo/stack/.env", "A=1\n");
        Write("repo/stack/keep.txt", "same\n");
        Sandbox.Git(repo, "init", "-q");
        Sandbox.Commit(repo, "known good");
        File.WriteAllText(repo + "/stack/.env", "A=2\nB=new\n");
        File.WriteAllText(repo + "/stack/untracked.txt", "x");
        var host = new LocalHost();

        var facts = await GitRepo.FactsAsync(host, repo + "/stack", CancellationToken.None);
        Assert.NotNull(facts);
        Assert.Equal("known good", facts!.Subject);
        Assert.Equal([" M stack/.env", "?? stack/untracked.txt"], facts.Changed.Order().ToArray());
        Assert.Null(await GitRepo.FactsAsync(host, _root, CancellationToken.None));                // not a repository

        // the stack folder as it was at HEAD, even though it is a subfolder of the repository
        var reference = await GitRepo.MaterialiseAsync(host, repo + "/stack", "HEAD", _root + "/refs", CancellationToken.None);
        var r = await DeploymentCompare.RunAsync(host, repo + "/stack", reference, 4, CancellationToken.None, ignoreLineEndings: true);

        Assert.Equal(1, r.Same);                                                                    // keep.txt, whatever its line endings became
        Assert.Contains(r.Diffs, d => d is { File: ".env", Item: "A", Kind: "changed", Current: "2", Reference: "1" });
        Assert.Contains(r.Diffs, d => d is { File: ".env", Item: "B", Kind: "added" });
        Assert.Contains(r.Diffs, d => d is { File: "untracked.txt", Area: "file", Kind: "added" });
        await Assert.ThrowsAsync<ArgumentException>(() => GitRepo.MaterialiseAsync(host, repo, "no-such-ref", _root + "/refs", CancellationToken.None));
    }
}

public class RemarkAndDependantsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-rem-" + Guid.NewGuid().ToString("N")[..8], "config");
    public RemarkAndDependantsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, true);

    [Fact]
    public void A_remark_sits_in_the_stream_between_runs_and_reads_as_a_note_in_the_spool()
    {
        History.Append(_dir, new RunRecord(DateTimeOffset.UtcNow, "local", ["debug", "x"], 0, "[1/1] a\n    OK   fine", new()));
        History.Remark(_dir, "  gateway is still on 8080; restart needed  ");

        var last = History.Last(_dir, 5);
        Assert.True(History.IsRemark(last[1]));
        Assert.False(History.IsRemark(last[0]));

        var text = Spool.Build(_dir, new RunState(), last, new SpoolOptions(5, [], false, false, null));
        Assert.Contains("--- run 1: toolrunner debug x", text);
        Assert.Contains("--- note 2 (written by me at", text);
        Assert.Contains("): gateway is still on 8080; restart needed", text);
    }

    [Fact]
    public async Task Dependants_are_found_through_other_services()
    {
        var root = Path.GetDirectoryName(_dir)!.Replace('\\', '/');
        File.WriteAllText(root + "/docker-compose.yml", "services:\n  db:\n    image: x\n  api:\n    image: x\n    depends_on: [db]\n  web:\n    image: x\n    depends_on:\n      - api\n  lone:\n    image: x\n");
        var c = new Catalog(root, await StackScanner.ScanDirectoryAsync(new LocalHost(), root, 1, CancellationToken.None), [], [], DateTimeOffset.Now);

        Assert.Equal(["api", "web"], c.Dependants("db"));
        Assert.Empty(c.Dependants("web"));
    }
}
