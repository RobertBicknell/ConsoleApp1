using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tests;

public class ComposeLayoutTests
{
    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    private static (List<PortMap> Ports, List<MountRef> Mounts, List<NamedVolume> Declared) Parse(string yaml, IReadOnlyDictionary<string, string>? env = null) =>
        ComposeLayout.Parse("c.yml", yaml, env ?? NoEnv);

    [Fact]
    public void Short_and_long_port_forms_are_all_understood()
    {
        var (ports, _, _) = Parse("""
            services:
              web:
                ports:
                  - "8080:80"
                  - 127.0.0.1:9090:90/udp
                  - 7000
                  - "8000-8002:8000-8002"
                  - target: 443
                    published: 8443
                    protocol: tcp
                    host_ip: 10.0.0.1
                expose:
                  - "9000"
            """);

        var got = ports.Select(p => (p.Kind, p.HostIp, p.Published, p.Target, p.Protocol)).ToArray();
        Assert.Equal(
        [
            ("publish", null, "8080", "80", "tcp"),
            ("publish", "127.0.0.1", "9090", "90", "udp"),
            ("publish", null, null, "7000", "tcp"),                // docker picks the host port
            ("publish", null, "8000-8002", "8000-8002", "tcp"),
            ("publish", "10.0.0.1", "8443", "443", "tcp"),
            ("expose", null, null, "9000", "tcp")
        ], got);
        Assert.All(ports, p => Assert.Equal("web", p.Service));
    }

    [Fact]
    public void Ports_are_expanded_from_the_env_and_unresolved_references_stay_visible()
    {
        var env = new Dictionary<string, string> { ["P"] = "9000" };
        var (ports, _, _) = Parse("services:\n  a:\n    ports:\n      - \"${P:-8080}:80\"\n      - \"${NOPE:-8081}:81\"\n      - \"${UNSET}:82\"\n", env);

        Assert.Equal(["9000", "8081", "${UNSET}"], ports.Select(p => p.Published!).ToArray());
    }

    [Fact]
    public void Mount_forms_are_classified_as_bind_named_anonymous_or_tmpfs()
    {
        var (_, mounts, _) = Parse("""
            services:
              a:
                volumes:
                  - ./data:/data
                  - ../up:/up:ro
                  - /abs/path:/abs
                  - ~/home:/home
                  - named:/n
                  - C:\data:/win
                  - /anon
                  - ${DIR}:/unres
                  - type: bind
                    source: ./long
                    target: /long
                    read_only: true
                  - type: volume
                    source: lv
                    target: /lv
                tmpfs:
                  - /run
            """);

        var got = mounts.Select(m => (m.Kind, m.Source, m.Target, m.ReadOnly)).ToArray();
        Assert.Equal(
        [
            ("bind", "./data", "/data", false),
            ("bind", "../up", "/up", true),
            ("bind", "/abs/path", "/abs", false),
            ("bind", "~/home", "/home", false),
            ("volume", "named", "/n", false),
            ("bind", @"C:\data", "/win", false),                   // the drive colon is not a separator
            ("anonymous", null, "/anon", false),
            ("unresolved", "${DIR}", "/unres", false),
            ("bind", "./long", "/long", true),
            ("volume", "lv", "/lv", false),
            ("tmpfs", null, "/run", false)
        ], got);
    }

    [Fact]
    public void Top_level_volumes_are_declared_and_external_ones_marked()
    {
        var (_, _, declared) = Parse("services:\n  a:\n    image: x\nvolumes:\n  plain:\n  ext:\n    external: true\n  notext:\n    external: false\n");

        Assert.Equal([("plain", false), ("ext", true), ("notext", false)], declared.Select(v => (v.Name, v.External)).ToArray());
    }

    [Fact]
    public void Entries_are_attributed_to_the_right_service_and_other_keys_do_not_leak_in()
    {
        var (ports, mounts, _) = Parse("services:\n  a:\n    ports:\n      - 1:1\n    image: x\n    environment:\n      - P=2:2\n  b:\n    volumes:\n      - v:/v\n    ports:\n      - 3:3\n");

        Assert.Equal([("a", "1"), ("b", "3")], ports.Select(p => (p.Service!, p.Published!)).ToArray());
        Assert.Equal([("b", "v")], mounts.Select(m => (m.Service!, m.Source!)).ToArray());
    }

    [Theory]
    [InlineData("/a/b/./c", "/a/b/c")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("C:/x/../y", "C:/y")]
    [InlineData("/a/../../b", "/b")]
    [InlineData(@"C:\a\.\b", "C:/a/b")]
    public void Collapse_normalises_dot_and_dotdot(string input, string expected) =>
        Assert.Equal(expected, ComposeLayout.Collapse(input));
}

public class StackScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolrunner-scan-" + Guid.NewGuid().ToString("N")[..8]).Replace('\\', '/');
    public StackScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Task<DirScan> Scan(int depth = 6) => StackScanner.ScanDirectoryAsync(new LocalHost(), _root, depth, CancellationToken.None);

    [Fact]
    public async Task Finds_stacks_recursively_skips_vendored_directories_and_honours_depth()
    {
        Write("a/docker-compose.yml", "services:\n  s:\n    image: x\n");
        Write("b/compose.yaml", "services:\n  s:\n    image: x\n");
        Write("a/node_modules/pkg/docker-compose.yml", "services:\n  s:\n    image: x\n");     // never entered
        Write("a/.git/docker-compose.yml", "services:\n  s:\n    image: x\n");
        Write("x/y/z/deep/docker-compose.yml", "services:\n  s:\n    image: x\n");

        Assert.Equal(["a", "b", "x/y/z/deep"], (await Scan()).Stacks.Select(s => s.Dir[(_root.Length + 1)..]).ToArray());
        Assert.Equal(["a", "b"], (await Scan(depth: 2)).Stacks.Select(s => s.Dir[(_root.Length + 1)..]).ToArray());
    }

    [Fact]
    public async Task An_override_file_joins_the_stack_of_its_base_file()
    {
        Write("app/docker-compose.yml", "services:\n  s:\n    ports:\n      - 80:80\n");
        Write("app/docker-compose.override.yml", "services:\n  s:\n    ports:\n      - 81:81\n");

        var stack = (await Scan()).Stacks.Single();

        Assert.Equal(2, stack.ComposeFiles.Count);
        Assert.Equal(["80", "81"], stack.Ports.Select(p => p.Published!).Order().ToArray());
    }

    [Fact]
    public async Task The_same_host_port_in_two_stacks_is_a_conflict_but_a_different_host_ip_is_not()
    {
        Write("a/docker-compose.yml", "services:\n  one:\n    ports:\n      - 5000:80\n      - 127.0.0.1:6000:80\n");
        Write("b/docker-compose.yml", "services:\n  two:\n    ports:\n      - 5000:81\n      - 127.0.0.2:6000:81\n");

        var conflicts = (await Scan()).Conflicts;

        var c = Assert.Single(conflicts);
        Assert.Equal(("*", "5000"), (c.HostIp, c.Published));
        Assert.Equal(2, c.Users.Count);
    }

    [Fact]
    public async Task Bind_sources_are_resolved_against_the_compose_directory_and_checked_for_existence()
    {
        Directory.CreateDirectory(Path.Combine(_root, "app", "data"));
        Write("app/docker-compose.yml", "services:\n  s:\n    volumes:\n      - ./data:/d\n      - ./nope:/n\n      - ../app/data:/again\n");

        var mounts = (await Scan()).Stacks.Single().Mounts;

        Assert.Equal(_root + "/app/data", mounts[0].Resolved);
        Assert.Equal([true, false, true], mounts.Select(m => m.Exists).ToArray());
        Assert.Equal(mounts[0].Resolved, mounts[2].Resolved);                 // ../app/data collapses to the same path
    }

    [Fact]
    public async Task The_env_file_beside_the_compose_file_feeds_interpolation_of_ports_and_bind_paths()
    {
        Write("app/.env", "WEB_PORT=7777\nDATA=./store\n");
        Write("app/docker-compose.yml", "services:\n  s:\n    ports:\n      - \"${WEB_PORT}:80\"\n    volumes:\n      - ${DATA}:/d\n");

        var stack = (await Scan()).Stacks.Single();

        Assert.Equal("7777", stack.Ports.Single().Published);
        Assert.Equal("bind", stack.Mounts.Single().Kind);
        Assert.Equal(_root + "/app/store", stack.Mounts.Single().Resolved);
    }

    [Fact]
    public async Task Env_files_nothing_refers_to_are_listed_as_unreferenced()
    {
        Write("app/docker-compose.yml", "services:\n  s:\n    env_file: s.env\n");
        Write("app/.env", "A=1\n");
        Write("app/s.env", "B=2\n");
        Write("app/stray.env", "C=3\n");

        var scan = await Scan();

        Assert.Equal([_root + "/app/stray.env"], scan.UnreferencedEnvFiles.ToArray());
    }

    [Fact]
    public async Task No_compose_files_is_reported_not_thrown()
    {
        var scan = await Scan();
        Assert.Empty(scan.Stacks);
        Assert.Contains(scan.Notes, n => n.Contains("no compose files"));
    }

    [Fact]
    public async Task The_text_summary_names_ports_conflicts_missing_binds_and_undeclared_volumes()
    {
        Write("a/docker-compose.yml", "services:\n  one:\n    ports:\n      - 5000:80\n    volumes:\n      - ./gone:/g\n      - cache:/c\n");
        Write("b/docker-compose.yml", "services:\n  two:\n    ports:\n      - 5000:81\n");

        var text = StackScanner.Text(await Scan());

        Assert.Contains("PORT CONFLICTS", text);
        Assert.Contains("a/gone -> /g", text.Replace("\\", "/"));
        Assert.Contains("MISSING", text);
        Assert.Contains("[NOT DECLARED]", text);
    }
}

public class HostSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolrunner-find-" + Guid.NewGuid().ToString("N")[..8]);
    public HostSearchTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task FindFiles_matches_names_case_insensitively_and_returns_forward_slashes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        File.WriteAllText(Path.Combine(_root, "d", "Docker-Compose.YML"), "");
        File.WriteAllText(Path.Combine(_root, "other.txt"), "");

        var found = await new LocalHost().FindFilesAsync(_root, ["docker-compose*.yml"], 3, CancellationToken.None);

        var one = Assert.Single(found);
        Assert.EndsWith("/d/Docker-Compose.YML", one);
        Assert.DoesNotContain('\\', one);
    }

    [Fact]
    public async Task Exists_is_true_for_files_and_directories_and_false_otherwise()
    {
        File.WriteAllText(Path.Combine(_root, "f"), "");
        var h = new LocalHost();

        Assert.True(await h.ExistsAsync(Path.Combine(_root, "f"), CancellationToken.None));
        Assert.True(await h.ExistsAsync(_root, CancellationToken.None));
        Assert.False(await h.ExistsAsync(Path.Combine(_root, "nope"), CancellationToken.None));
    }
}
