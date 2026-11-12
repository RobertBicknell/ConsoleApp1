using ToolRunner.Core;

namespace ToolRunner.Tests;

public class SpoolAndHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "toolrunner-spool-" + Guid.NewGuid().ToString("N")[..8], "config");
    public SpoolAndHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_dir)!, true);

    private static RunRecord Run(string cmd, int exit = 0, string log = "[1/1] x\n    OK   fine") =>
        new(DateTimeOffset.UtcNow, "local", cmd.Split(' '), exit, log, new());

    private static RunState State(params (string K, string V)[] v)
    {
        var s = new RunState();
        foreach (var (k, val) in v) s.Vars[k] = val;
        return s;
    }

    [Fact]
    public void History_round_trips_and_returns_the_last_n_oldest_first()
    {
        foreach (var c in new[] { "one", "two", "three" }) History.Append(_dir, Run(c));

        Assert.Equal(["two", "three"], History.Last(_dir, 2).Select(r => r.Argv[0]).ToArray());
    }

    [Fact]
    public void A_half_written_history_line_is_skipped_not_fatal()
    {
        History.Append(_dir, Run("good"));
        File.AppendAllText(Path.Combine(History.Dir(_dir), "history.jsonl"), "{\"At\": \"broken");

        Assert.Equal(["good"], History.Last(_dir, 5).Select(r => r.Argv[0]).ToArray());
    }

    [Fact]
    public void Spool_has_its_sections_in_order_with_the_answer_format_last()
    {
        var text = Spool.Build(_dir, State(("A", "1")), [Run("debug pluginhost")], new SpoolOptions(5, [], false, false, "why?"));

        var order = new[] { "##### INSTRUCTIONS", "##### MY QUESTION", "##### RECENT RUNS", "##### VARIABLES", "##### REQUIRED ANSWER FORMAT" }
            .Select(h => text.IndexOf(h, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.Order().ToArray(), order);
        Assert.Contains("toolrunner debug pluginhost", text);
        Assert.Contains("why?", text);
    }

    [Fact]
    public void Spool_hides_secret_looking_variables_and_expands_the_rest()
    {
        var text = Spool.Build(_dir, State(("DB_PASSWORD", "hunter2"), ("ROOT", "/srv"), ("FILE", "${ROOT}/x")), [], new SpoolOptions(5, [], false, false, null));

        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("DB_PASSWORD = ********", text);
        Assert.Contains("FILE = /srv/x", text);
    }

    [Fact]
    public void Defang_replaces_angle_brackets_everywhere_including_included_files()
    {
        var file = Path.Combine(_dir, "x.xml");
        File.WriteAllText(file, "<a><b/></a>");

        var text = Spool.Build(_dir, State(), [], new SpoolOptions(5, [file], false, true, null));

        Assert.DoesNotContain('<', text);
        Assert.DoesNotContain('>', text);
        Assert.Contains("‹a›‹b/›‹/a›", text);
    }

    [Fact]
    public async Task A_directory_scan_is_included_as_its_own_section_without_variable_values()
    {
        var root = Path.GetDirectoryName(_dir)!.Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", ".env"), "DB_PASSWORD=hunter2\n");
        File.WriteAllText(Path.Combine(root, "app", "docker-compose.yml"), "services:\n  s:\n    ports:\n      - 5000:80\n    environment:\n      - DB_PASSWORD=${DB_PASSWORD}\n");
        var scan = await StackScanner.ScanDirectoryAsync(new ToolRunner.Hosts.LocalHost(), root, 3, CancellationToken.None);

        var text = Spool.Build(_dir, State(), [], new SpoolOptions(5, [], false, false, null), dirScan: scan);

        Assert.Contains("##### STACK SCAN", text);
        Assert.Contains("*:5000", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.True(text.IndexOf("##### STACK SCAN", StringComparison.Ordinal) < text.IndexOf("##### REQUIRED ANSWER FORMAT", StringComparison.Ordinal));
    }

    [Fact]
    public void A_previous_summary_is_included_when_carry_txt_exists()
    {
        Directory.CreateDirectory(History.Dir(_dir));
        File.WriteAllText(Path.Combine(History.Dir(_dir), "carry.txt"), "we ruled out the port");

        var text = Spool.Build(_dir, State(), [], new SpoolOptions(5, [], false, false, null));

        Assert.Contains("##### PREVIOUS SUMMARY", text);
        Assert.Contains("we ruled out the port", text);
    }
}
