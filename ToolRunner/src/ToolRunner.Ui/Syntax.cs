using System.Text.RegularExpressions;

namespace ToolRunner.Ui;

/// Light syntax colouring for the kinds of file this tool shows: JSON, YAML (compose), env files and XML.
/// One line at a time, regex-based: good enough to make structure visible, not a parser.
/// Search matches are overlaid on top, so a match may span several tokens.
public static class Syntax
{
    public sealed record Seg(string Text, string Class, bool Marked);

    private static readonly Dictionary<string, Regex> Rules = new()
    {
        ["json"] = new(@"(?<key>""(?:\\.|[^""\\])*""(?=\s*:))|(?<var>\$\{[^}]*\})|(?<str>""(?:\\.|[^""\\])*"")|(?<num>-?\b\d+(?:\.\d+)?\b)|(?<kw>\b(?:true|false|null)\b)", RegexOptions.Compiled),
        ["yaml"] = new(@"(?<cm>(?:^|(?<=\s))#.*$)|(?<var>\$\{[^}]*\})|(?<key>(?<=^\s*(?:-\s+)?)[\w.\-/]+(?=\s*:(?:\s|$)))|(?<str>""[^""]*""|'[^']*')|(?<kw>(?<=:\s+|-\s+)(?:true|false|null|yes|no)\b)|(?<num>(?<=:\s+|-\s+)-?\d+(?:\.\d+)?(?=\s*(?:#|$)))", RegexOptions.Compiled),
        ["env"] = new(@"(?<cm>^\s*#.*$)|(?<var>\$\{[^}]*\})|(?<key>(?<=^\s*(?:export\s+)?)[A-Za-z_]\w*(?==))|(?<str>(?<==).+$)", RegexOptions.Compiled),
        ["xml"] = new(@"(?<cm><!--.*?-->)|(?<var>\$\{[^}]*\})|(?<key></?[\w:.\-]+|/?>|\?>)|(?<kw>[\w:.\-]+(?==""))|(?<str>""[^""]*"")", RegexOptions.Compiled)
    };
    private static readonly string[] Groups = { "cm", "var", "key", "str", "num", "kw" };

    public static string Language(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        var ext = Path.GetExtension(n);
        return ext == ".json" ? "json"
             : ext is ".yml" or ".yaml" ? "yaml"
             : ext == ".env" || Path.GetFileName(n).StartsWith(".env") ? "env"
             : ext is ".xml" or ".config" or ".csproj" or ".props" or ".targets" or ".xaml" or ".nuspec" or ".resx" ? "xml"
             : "text";
    }

    public static List<Seg> Line(string line, string language, Regex? term = null)
    {
        // 1. tokens: (start, end, class), gaps are plain
        var tokens = new List<(int Start, int End, string Class)>();
        if (Rules.TryGetValue(language, out var rule))
            foreach (Match m in rule.Matches(line))
            {
                if (m.Length == 0) continue;
                var cls = Groups.FirstOrDefault(g => m.Groups[g].Success);
                if (cls is not null) tokens.Add((m.Index, m.Index + m.Length, "s-" + cls));
            }

        // 2. search marks
        var marks = new List<(int Start, int End)>();
        if (term is not null)
            foreach (Match m in term.Matches(line)) if (m.Length > 0) marks.Add((m.Index, m.Index + m.Length));

        // 3. cut at every boundary
        var cuts = new SortedSet<int> { 0, line.Length };
        foreach (var t in tokens) { cuts.Add(t.Start); cuts.Add(t.End); }
        foreach (var k in marks) { cuts.Add(k.Start); cuts.Add(k.End); }
        var points = cuts.ToList();
        var segs = new List<Seg>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, b) = (points[i], points[i + 1]);
            var cls = tokens.FirstOrDefault(t => t.Start <= a && b <= t.End).Class ?? "";
            segs.Add(new Seg(line[a..b], cls, marks.Any(k => k.Start <= a && b <= k.End)));
        }
        return segs;
    }
}
