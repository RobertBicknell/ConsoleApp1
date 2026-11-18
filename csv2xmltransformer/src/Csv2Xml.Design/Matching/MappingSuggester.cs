using Csv2Xml.Design.Schema;

namespace Csv2Xml.Design.Matching;

/// <summary>Proposed pairing of a target to a CSV column. ColumnIndex null = no confident match.</summary>
internal sealed record Suggestion(SchemaTarget Target, int? ColumnIndex, string? Header, double Score);

internal sealed record KindMatch(SchemaKind Kind, double Score, IReadOnlyList<Suggestion> Suggestions);

internal static class MappingSuggester
{
    public const double DefaultThreshold = 0.7;

    /// <summary>Greedy one-to-one assignment by descending score; unassigned targets/columns are left unmatched.</summary>
    public static IReadOnlyList<Suggestion> Suggest(IReadOnlyList<string> headers, SchemaKind kind, double threshold = DefaultThreshold)
    {
        var pairs = (from t in kind.Targets
                     from c in Enumerable.Range(0, headers.Count)
                     let s = NameMatcher.Score(headers[c], t)
                     where s >= threshold
                     orderby s descending, c, t.Path
                     select (t, c, s)).ToList();

        var byTarget = new Dictionary<string, (int c, double s)>();
        var usedCols = new HashSet<int>();
        foreach (var (t, c, s) in pairs)
        {
            if (byTarget.ContainsKey(t.Path) || !usedCols.Add(c)) continue;
            byTarget[t.Path] = (c, s);
        }

        return kind.Targets
            .Select(t => byTarget.TryGetValue(t.Path, out var m)
                ? new Suggestion(t, m.c, headers[m.c], m.s)
                : new Suggestion(t, null, null, 0))
            .ToList();
    }

    /// <summary>Fitness of a kind: required targets weigh double; result is matched weight / total weight.</summary>
    public static KindMatch Evaluate(IReadOnlyList<string> headers, SchemaKind kind, double threshold = DefaultThreshold)
    {
        var sug = Suggest(headers, kind, threshold);
        double W(Suggestion s) => s.Target.Required ? 2 : 1;
        var total = sug.Sum(W);
        var got = sug.Where(s => s.ColumnIndex is not null).Sum(s => W(s) * s.Score);
        return new KindMatch(kind, total == 0 ? 0 : got / total, sug);
    }

    /// <summary>Rank kinds for a CSV header set, best first.</summary>
    public static IReadOnlyList<KindMatch> Classify(IReadOnlyList<string> headers, IEnumerable<SchemaKind> kinds, double threshold = DefaultThreshold) =>
        kinds.Select(k => Evaluate(headers, k, threshold)).OrderByDescending(m => m.Score).ToList();
}
