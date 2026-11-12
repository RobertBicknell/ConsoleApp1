using System.Text.RegularExpressions;

namespace ToolRunner.Ui;

/// One step of a run as shown in the UI: the "[i/n] tool/intent" line and everything logged under it.
public sealed record StepCard(string Header, List<string> Lines, string Status);

public static class LogView
{
    private static readonly Regex Marker = new(@"^\[\d+/\d+\]\s", RegexOptions.Compiled);

    /// Splits a toolrunner log into one card per step. Status: fail if any FAIL line, ok if an OK line, else running.
    public static List<StepCard> Cards(IEnumerable<string> log)
    {
        var cards = new List<(string Header, List<string> Lines)>();
        foreach (var line in log)
        {
            if (Marker.IsMatch(line) || cards.Count == 0) cards.Add((Marker.IsMatch(line) ? line : "log", new()));
            if (!Marker.IsMatch(line)) cards[^1].Lines.Add(line);
        }
        return cards.Select(c => new StepCard(c.Header, c.Lines, StatusOf(c.Lines))).ToList();
    }

    private static string StatusOf(List<string> lines) =>
        lines.Any(l => l.StartsWith("    FAIL") || l.StartsWith("BLOCKED ")) ? "fail" :
        lines.Any(l => l.StartsWith("    OK")) ? "ok" :
        lines.Any(l => l.StartsWith("    WOULD RUN")) ? "dry" : "running";
}
