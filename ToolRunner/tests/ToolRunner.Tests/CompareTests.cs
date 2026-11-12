using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tests;

/// Comparing a deployment folder with a reference folder, by meaning.
public class CompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "toolrunner-cmp-" + Guid.NewGuid().ToString("N")[..8]).Replace('\\', '/');
    public CompareTests() { Directory.CreateDirectory(_root + "/cur"); Directory.CreateDirectory(_root + "/ref"); }
    public void Dispose() => Directory.Delete(_root, true);

    private void Cur(string rel, string text) => Write("cur", rel, text);
    private void Ref(string rel, string text) => Write("ref", rel, text);
    private void Both(string rel, string text) { Cur(rel, text); Ref(rel, text); }
    private void Write(string side, string rel, string text)
    {
        var path = $"{_root}/{side}/{rel}";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private Task<CompareResult> Run() => DeploymentCompare.RunAsync(new LocalHost(), _root + "/cur", _root + "/ref", 8, CancellationToken.None);
    private static (string, string, string?, string?)[] Rows(CompareResult r, string file) =>
        r.Diffs.Where(d => d.File == file).Select(d => (d.Kind, d.Item, d.Current, d.Reference)).ToArray();

    [Fact]
    public async Task Files_on_one_side_only_are_reported_and_identical_files_are_counted()
    {
        Both("same.txt", "x\n");
        Cur("extra/new.txt", "x\n");
        Ref("gone.txt", "x\n");

        var r = await Run();

        Assert.Equal((1, 1, 1, 0), (r.Same, r.FilesOnlyHere, r.FilesOnlyInReference, r.FilesDifferent));
        Assert.Contains(r.Diffs, d => d is { Area: "file", File: "extra/new.txt", Kind: "added" });
        Assert.Contains(r.Diffs, d => d is { Area: "file", File: "gone.txt", Kind: "removed" });
    }

    [Fact]
    public async Task Env_files_are_compared_by_key_ignoring_order_and_comments()
    {
        Cur(".env", "# edited\nB=2\nA=1\nNEW=x\nDB_PASSWORD=hunter2\n");
        Ref(".env", "A=1\nB=9\nOLD=y\nDB_PASSWORD=original\n");

        var rows = Rows(await Run(), ".env");

        Assert.Equal(
        [
            ("changed", "B", "2", "9"),
            ("changed", "DB_PASSWORD", "hunter2", "original"),
            ("added", "NEW", "x", null),
            ("removed", "OLD", null, "y")
        ], rows);
    }

    [Fact]
    public async Task Json_settings_are_compared_by_config_path_and_lists_of_values_as_sets()
    {
        Cur("appsettings.json", """{ "Logging": { "Level": "Debug" }, "Plugins": { "Assemblies": [ "A", "C", "B" ] }, "New": 1 }""");
        Ref("appsettings.json", """
            // reference
            { "Plugins": { "Assemblies": [ "A", "B", "D" ] }, "Logging": { "Level": "Information" } }
            """);

        var rows = Rows(await Run(), "appsettings.json");

        Assert.Equal(
        [
            ("changed", "Logging:Level", "Debug", "Information"),
            ("added", "New", "1", null),
            ("added", "Plugins:Assemblies[] C", "(listed)", null),       // inserting C does not make B look changed
            ("removed", "Plugins:Assemblies[] D", null, "(listed)")
        ], rows);
    }

    [Fact]
    public async Task Compose_files_are_compared_by_service_image_ports_mounts_and_dependencies()
    {
        Ref(".env", "TAG=1.0\n");
        Cur(".env", "TAG=2.0\n");
        Ref("docker-compose.yml", "services:\n  web:\n    image: acme/web:${TAG}\n    ports:\n      - 8080:80\n    depends_on:\n      - cache\n  cache:\n    image: redis:7\n    ports:\n      - 6379:6379\n");
        Cur("docker-compose.yml", "services:\n  web:\n    image: acme/web:${TAG}\n    ports:\n      - 8081:80\n    volumes:\n      - ./hotfix:/app/hotfix\n  metrics:\n    image: prom:1\n");

        var rows = Rows(await Run(), "docker-compose.yml").Select(x => $"{x.Item1} {x.Item2}").ToArray();

        Assert.Equal(
        [
            "removed service cache",                    // one line for the whole service, not one per port
            "added service metrics",
            "added web bind ./hotfix -> /app/hotfix",
            "removed web depends_on cache",
            "removed web port *:8080 -> 80/tcp",
            "added web port *:8081 -> 80/tcp",
            "changed web.image"                         // same text, but each side's own .env gives a different tag
        ], rows);
    }

    [Fact]
    public async Task Changes_that_do_not_change_meaning_are_named_as_such()
    {
        Ref("docker-compose.yml", "services:\n  a:\n    image: x\n    ports:\n      - \"80:80\"\n");
        Cur("docker-compose.yml", "# tidied\nservices:\n  a:\n    image: x\n    ports: [\"80:80\"]\n");
        Ref("nginx.conf", "a\nb\n");
        Cur("nginx.conf", "a\r\nb\r\n");
        Ref("x.env", "A=1\n");
        Cur("x.env", "# note\nA=1\n");

        var r = await Run();

        Assert.Equal([("changed", "comments or formatting only", null, null)], Rows(r, "docker-compose.yml"));
        Assert.Equal([("changed", "line endings only", "CRLF", "LF")], Rows(r, "nginx.conf"));
        Assert.Equal([("changed", "comments or formatting only", null, null)], Rows(r, "x.env"));
    }

    [Fact]
    public async Task Binaries_are_compared_by_hash_and_other_text_by_line_counts()
    {
        Both("plugins/Same.dll", "abc");
        Cur("plugins/Changed.dll", "build-2");
        Ref("plugins/Changed.dll", "build-1");
        Cur("notes.txt", "one\ntwo\nthree\n");
        Ref("notes.txt", "one\nTWO\n");

        var r = await Run();

        var bin = Assert.Single(r.Diffs, d => d.Area == "binary");
        Assert.Equal("plugins/Changed.dll", bin.File);
        Assert.NotEqual(bin.Current, bin.Reference);
        Assert.Equal(("2 line(s) only here", "1 line(s) only in the reference"), (Rows(r, "notes.txt")[0].Item3, Rows(r, "notes.txt")[0].Item4));
        Assert.Equal(1, r.Same);
    }

    [Fact]
    public async Task A_missing_folder_is_a_note_not_an_exception()
    {
        var r = await DeploymentCompare.RunAsync(new LocalHost(), _root + "/cur", _root + "/nope", 8, CancellationToken.None);

        Assert.Empty(r.Diffs);
        Assert.Contains("reference folder not found", Assert.Single(r.Notes));
    }

    [Fact]
    public async Task The_text_summary_groups_by_file_and_hides_secrets_only_when_asked()
    {
        Cur(".env", "DB_PASSWORD=hunter2\nA=1\n");
        Ref(".env", "DB_PASSWORD=original\nA=2\n");
        Cur("only-here.txt", "");

        var r = await Run();
        var text = DeploymentCompare.Text(r, hideSecrets: true);       // what the spool file uses

        Assert.Contains("ONLY HERE", text);
        Assert.Contains("changed  A: 1   (reference: 2)", text);
        Assert.Contains("changed  DB_PASSWORD: ********   (reference: ********)", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("original", text);
        Assert.Contains("changed  DB_PASSWORD: hunter2   (reference: original)", DeploymentCompare.Text(r));       // the screen shows it
    }
}
