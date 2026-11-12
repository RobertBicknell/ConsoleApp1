using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tests;

public class SystemMapTests
{
    private static Stack Stack(params (string Name, string[] Deps)[] services) => new("/deploy/core", ["c.yml"],
        [
            new ScanRow("ORDERS_URL", "env", "ok", "http://orders-api:8080/api", [new VarRef("ORDERS_URL", "environment", "gateway", null, "c.yml", 9)]),
            new ScanRow("DB_HOST", "env", "ok", "db", [new VarRef("DB_HOST", "environment", "orders-api", null, "c.yml", 20), new VarRef("DB_HOST", "env-file", null, "db", ".env", 1)]),
            new ScanRow("LOG_LEVEL", "env", "ok", "info", [new VarRef("LOG_LEVEL", "environment", "gateway", null, "c.yml", 10)])
        ],
        [new PortMap("c.yml", 5, "gateway", "publish", null, "8081", "80", "tcp", "8081:80")],
        [new MountRef("c.yml", 7, "gateway", "bind", "./config", "/deploy/core/config", "/app/config", true, "./config:/app/config:ro", false)],
        [], services.Select((s, i) => new ServiceInfo(s.Name, $"acme/{s.Name}:1.0", null, s.Deps, false, "c.yml", i + 1)).ToList(), [], []);

    [Fact]
    public void Entry_points_are_on_the_left_and_each_column_holds_what_the_previous_one_needs()
    {
        var map = SystemMap.Build(Stack(("gateway", ["pluginhost"]), ("pluginhost", ["db"]), ("orders-api", []), ("db", [])));

        int Col(string s) => map.Nodes.Single(n => n.Service == s).Column;
        Assert.Equal((0, 1, 2), (Col("gateway"), Col("pluginhost"), Col("db")));
        Assert.Equal(1, Col("orders-api"));                                           // gateway talks to it, it talks to db
        Assert.Equal(3, map.Columns);
        Assert.All(map.Links, l => Assert.True(Col(l.To) > Col(l.From)));              // every line runs left to right
    }

    [Fact]
    public void A_setting_that_names_another_service_is_a_connection_and_says_which_setting()
    {
        var map = SystemMap.Build(Stack(("gateway", ["pluginhost"]), ("pluginhost", []), ("orders-api", ["db"]), ("db", [])));

        var talks = Assert.Single(map.Links, l => l.Kind == "talks");
        Assert.Equal(("gateway", "orders-api", "ORDERS_URL"), (talks.From, talks.To, talks.Via[0]));
        var dep = map.Links.Single(l => l.From == "orders-api" && l.To == "db");
        Assert.Equal(("depends", "DB_HOST"), (dep.Kind, dep.Via[0]));                  // depends_on wins; the setting is still named
    }

    [Fact]
    public void A_node_carries_its_key_facts_and_a_dependency_cycle_does_not_hang()
    {
        var map = SystemMap.Build(Stack(("gateway", ["pluginhost"]), ("pluginhost", ["gateway"]), ("orders-api", []), ("db", [])));
        var gateway = map.Nodes.Single(n => n.Service == "gateway");
        Assert.Equal(["8081:80"], gateway.Ports);
        Assert.Equal((1, 1), (gateway.Binds, gateway.MissingBinds));
        Assert.Equal(["pluginhost"], gateway.Dependants);
        Assert.Equal(4, map.Nodes.Count);
    }

    [Fact]
    public async Task Folders_are_listed_with_their_parent_and_whether_they_hold_a_compose_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolrunner-pick-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "core")); Directory.CreateDirectory(Path.Combine(dir, "reporting"));
        File.WriteAllText(Path.Combine(dir, "core", "docker-compose.yml"), "services: {}\n");
        try
        {
            var top = await Folders.ListAsync(new LocalHost(), dir, CancellationToken.None);
            Assert.Equal(["core", "reporting"], top.Folders);
            Assert.False(top.HasCompose);
            Assert.NotEmpty(top.Roots);
            var core = await Folders.ListAsync(new LocalHost(), top.Path + "/core", CancellationToken.None);
            Assert.True(core.HasCompose);
            Assert.Equal(top.Path, core.Parent);
            Assert.NotNull((await Folders.ListAsync(new LocalHost(), dir + "/nope", CancellationToken.None)).Error);
        }
        finally { Directory.Delete(dir, true); }
    }
}
