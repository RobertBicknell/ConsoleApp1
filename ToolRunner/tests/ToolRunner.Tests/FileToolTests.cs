using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// file/derive: writes a modified copy, never edits in place, and refuses unless every edit matches what it expects.
public class FileToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-file-" + Guid.NewGuid().ToString("N")[..8]);
    private string P(string name) => Path.Combine(_dir, name);
    public FileToolTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private async Task<ToolResult> Derive(string source, string edits, string? target = null)
    {
        var args = new JsonObject
        {
            ["verb"] = "derive", ["source"] = source, ["target"] = target ?? P("out"), ["edits"] = JsonNode.Parse(edits)
        };
        return await new FileTool().RunAsync(new ToolContext { State = new RunState(), Args = args, Host = new LocalHost(), Log = _ => { } });
    }

    [Fact]
    public async Task Set_replaces_only_the_named_key_and_leaves_the_source_untouched()
    {
        File.WriteAllText(P("in"), "A=1\nB=2\n");
        var r = await Derive(P("in"), """[{"set":"A","value":"9"}]""");

        Assert.True(r.Ok, r.Message);
        var lines = File.ReadAllLines(P("out"));
        Assert.StartsWith("# GENERATED", lines[0]);
        Assert.Equal(["A=9", "B=2"], lines[1..3]);
        Assert.Equal("A=1\nB=2\n", File.ReadAllText(P("in")));
    }

    [Fact]
    public async Task An_edit_that_matches_the_wrong_number_of_lines_writes_nothing()
    {
        File.WriteAllText(P("in"), "A=1\nB=2\n");
        var r = await Derive(P("in"), """[{"set":"A","value":"9"},{"set":"MISSING","value":"x"}]""");

        Assert.False(r.Ok);
        Assert.Contains("expected exactly 1 line, found 0", r.Message);
        Assert.False(File.Exists(P("out")));                    // the first edit was valid, but all-or-nothing
    }

    [Fact]
    public async Task Derive_refuses_to_write_over_its_own_source()
    {
        File.WriteAllText(P("in"), "A=1\n");
        var r = await Derive(P("in"), """[{"set":"A","value":"2"}]""", target: P("in"));

        Assert.False(r.Ok);
        Assert.Equal("A=1\n", File.ReadAllText(P("in")));
    }

    [Fact]
    public async Task Mixed_line_endings_still_match_and_are_rejoined_in_the_dominant_style()   // regression
    {
        File.WriteAllText(P("in"), "A=1\r\nB=2\nC=3\r\n");
        var r = await Derive(P("in"), """[{"set":"B","value":"7"}]""");

        Assert.True(r.Ok, r.Message);
        var text = File.ReadAllText(P("out"));
        Assert.Equal(["A=1", "B=7", "C=3"], text.Split("\r\n")[1..4]);       // no line lost or merged (the old bug swallowed C=3)
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));              // every newline is now CRLF
    }

    [Fact]
    public async Task Commented_out_lines_never_match_a_set()
    {
        File.WriteAllText(P("in"), "# A=old\nA=1\n");
        var r = await Derive(P("in"), """[{"set":"A","value":"2"}]""");

        Assert.True(r.Ok, r.Message);
        Assert.Contains("# A=old", File.ReadAllText(P("out")));
        Assert.Contains("A=2", File.ReadAllText(P("out")));
    }

    [Fact]
    public async Task CommentService_comments_exactly_that_services_block()
    {
        File.WriteAllText(P("in"), "services:\n  web:\n    image: w\n    ports:\n      - 80:80\n  db:\n    image: d\nvolumes:\n  v: {}\n");
        var r = await Derive(P("in"), """[{"commentService":"web"}]""");

        Assert.True(r.Ok, r.Message);
        var lines = File.ReadAllLines(P("out"));
        Assert.Equal(["#  web:", "#    image: w", "#    ports:", "#      - 80:80", "  db:", "    image: d"], lines[2..8]);
        Assert.Equal("volumes:", lines[8]);
    }

    [Fact]
    public async Task Match_edit_replaces_text_inside_the_expected_number_of_lines()
    {
        File.WriteAllText(P("in"), "port 5000\nhost x\nport 5000\n");

        Assert.False((await Derive(P("in"), """[{"match":"5000","replace":"5005"}]""")).Ok);      // expects 1, finds 2
        Assert.True((await Derive(P("in"), """[{"match":"5000","replace":"5005","expect":2}]""")).Ok);
        Assert.Equal(2, File.ReadAllText(P("out")).Split("5005").Length - 1);
    }
}
