using System.Text;
using Csv2Xml.Design.Schema;

namespace Csv2Xml.Design.Matching;

/// <summary>Lexical similarity between a CSV header and a schema target. Pure functions, score in [0,1].</summary>
internal static class NameMatcher
{
    /// <summary>Lowercase alphanumerics only: "Order_ID", "orderId", "ORDER ID" -> "orderid".</summary>
    public static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Split on non-alphanumerics and camelCase boundaries.</summary>
    public static IReadOnlyList<string> Tokens(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        void Flush() { if (sb.Length > 0) { tokens.Add(sb.ToString().ToLowerInvariant()); sb.Clear(); } }
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1])))) Flush();
            sb.Append(c);
        }
        Flush();
        return tokens;
    }

    public static double Similarity(string a, string b)
    {
        var na = Normalize(a); var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1.0;

        var lev = 1.0 - (double)Levenshtein(na, nb) / Math.Max(na.Length, nb.Length);

        var ta = Tokens(a).ToHashSet(); var tb = Tokens(b).ToHashSet();
        var jac = ta.Count == 0 || tb.Count == 0 ? 0 : (double)ta.Intersect(tb).Count() / ta.Union(tb).Count();

        return Math.Max(lev * 0.95, jac * 0.9);
    }

    /// <summary>Best score of the header against the target's leaf name and its path suffixes (e.g. "City", "AddressCity", "CustomerAddressCity").</summary>
    public static double Score(string header, SchemaTarget t)
    {
        var segs = t.Segments.Select(s => s.TrimStart('@')).ToArray();
        var best = 0.0;
        for (var len = 1; len <= segs.Length; len++)
        {
            var candidate = string.Concat(segs[^len..]);
            best = Math.Max(best, Similarity(header, candidate) - 0.02 * (len - 1));
        }
        return Math.Clamp(best, 0, 1);
    }

    static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
