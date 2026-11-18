using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Csv2Xml.Design.Schema;

namespace Csv2Xml.Design.Samples;

/// <summary>A value and whether it is known to satisfy the target's declared restrictions.</summary>
internal sealed record SampleValue(string Text, bool Certain);

/// <summary>Synthetic values that honour a target's type and facets; deterministic in the ordinal n (1-based).</summary>
internal static class SyntheticValues
{
    public static SampleValue For(SchemaTarget t, int n)
    {
        var f = t.Facets ?? Facets.None;
        if (f.Fixed is not null) return new(f.Fixed, true);
        if (f.Enumeration.Count > 0) return new(f.Enumeration[(n - 1) % f.Enumeration.Count], true);
        if (f.Patterns.Count > 0)
        {
            var v = PatternSample.Generate(f.Patterns[0], n);
            var ok = v is not null && f.Patterns.All(p => PatternSample.Matches(p, v)) && LengthOk(v, f);
            return new(v ?? Text(t.Name, n, f), ok);
        }
        return t.DataType switch
        {
            "boolean" => new(n % 2 == 1 ? "true" : "false", true),
            "date" => new(Day(n).ToString("yyyy-MM-dd"), true),
            "datetime" => new(Day(n).ToString("yyyy-MM-dd") + "T10:00:00Z", true),
            "time" => new($"{8 + n % 10:00}:00:00", true),
            "gyear" => new((2020 + n % 6).ToString(), true),
            "anyuri" => new($"https://example.com/{n}", true),
            "duration" => new($"P{n}D", true),
            "decimal" or "double" or "float" => Number(n, f, fraction: true),
            "int" or "integer" or "long" or "short" or "byte" or "unsignedint" or "unsignedlong" or "unsignedshort" or "unsignedbyte"
                or "nonnegativeinteger" or "positiveinteger" or "negativeinteger" or "nonpositiveinteger" => Number(n, f, fraction: false, t.DataType),
            _ => new(Text(t.Name, n, f), true),
        };
    }

    static DateTime Day(int n) => new DateTime(2024, 1, 1).AddDays(n - 1);

    static bool LengthOk(string v, Facets f) =>
        (f.Length is null || v.Length == f.Length) && (f.MinLength is null || v.Length >= f.MinLength) && (f.MaxLength is null || v.Length <= f.MaxLength);

    /// <summary>Name + ordinal, fitted to length facets (ordinal kept so values stay distinct).</summary>
    static string Text(string name, int n, Facets f)
    {
        var suffix = n.ToString();
        var max = f.Length ?? f.MaxLength ?? int.MaxValue;
        var min = f.Length ?? f.MinLength ?? 0;
        var stem = name.TrimStart('@');
        if (stem.Length + suffix.Length > max) stem = stem[..Math.Max(0, max - suffix.Length)];
        var s = stem + suffix;
        if (s.Length > max) s = s[^max..];
        return s.PadRight(min, 'x');
    }

    static SampleValue Number(int n, Facets f, bool fraction, string type = "decimal")
    {
        decimal? P(string? s) => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var typeMin = type switch { "positiveinteger" => 1m, "nonnegativeinteger" or "unsignedint" or "unsignedlong" or "unsignedshort" or "unsignedbyte" => 0m, _ => (decimal?)null };
        var typeMax = type switch { "negativeinteger" => -1m, "nonpositiveinteger" => 0m, "byte" => 127m, "unsignedbyte" => 255m, _ => (decimal?)null };
        var step = fraction && f.FractionDigits != 0 ? 0.01m : 1m;
        var lo = P(f.MinInclusive) ?? (P(f.MinExclusive) + step) ?? typeMin ?? (typeMax is { } tm ? tm - 1000 : 1m);
        var hi = P(f.MaxInclusive) ?? (P(f.MaxExclusive) - step) ?? typeMax ?? lo + 1000;
        if (f.TotalDigits is { } td) hi = Math.Min(hi, (decimal)Math.Pow(10, td - (fraction ? Math.Min(f.FractionDigits ?? 2, 2) : 0)) - 1);
        if (hi < lo) return new(lo.ToString(CultureInfo.InvariantCulture), false);

        var span = Math.Floor(hi - lo);
        var whole = lo + (span <= 0 ? 0 : (n * 7) % (span + 1));
        var frac = fraction && (f.FractionDigits ?? 2) > 0 && whole + 0.5m <= hi ? 0.5m : 0m;
        var digits = fraction ? Math.Min(f.FractionDigits ?? 2, 2) : 0;
        var v = Math.Round(whole + frac, digits);
        return new(v.ToString(digits == 0 ? "0" : "0." + new string('0', digits), CultureInfo.InvariantCulture), true);
    }
}

/// <summary>
/// Produces a string matching a simple XSD pattern: literals, escapes (\d \w \s \. etc.), [classes] with ranges,
/// quantifiers (? * + {n} {n,m}), groups and alternation (first branch). Returns null for anything else.
/// </summary>
internal static class PatternSample
{
    public static bool Matches(string pattern, string value)
    {
        try { return Regex.IsMatch(value, "^(?:" + pattern + ")$"); } catch (ArgumentException) { return false; }
    }

    public static string? Generate(string pattern, int n)
    {
        try
        {
            var pos = 0;
            var s = Seq(pattern, ref pos, n);
            return pos == pattern.Length && Matches(pattern, s) ? s : null;
        }
        catch (FormatException) { return null; }
    }

    static string Seq(string p, ref int i, int n)
    {
        var sb = new StringBuilder();
        while (i < p.Length && p[i] != ')')
        {
            if (p[i] == '|') { SkipToGroupEnd(p, ref i); break; } // take first alternative
            var atom = Atom(p, ref i, n);
            var (min, max) = Quantifier(p, ref i);
            var count = Math.Max(min, Math.Min(max, min == 0 ? 1 : min));
            for (var k = 0; k < count; k++) sb.Append(atom(k));
        }
        return sb.ToString();
    }

    static void SkipToGroupEnd(string p, ref int i)
    {
        var depth = 0;
        for (; i < p.Length; i++)
        {
            if (p[i] == '\\') { i++; continue; }
            if (p[i] == '(') depth++;
            else if (p[i] == ')') { if (depth == 0) return; depth--; }
        }
    }

    /// <summary>Returns a generator for one repetition of the atom (k = repetition index, varies output with n).</summary>
    static Func<int, string> Atom(string p, ref int i, int n)
    {
        var c = p[i++];
        switch (c)
        {
            case '(':
                if (i + 1 < p.Length && p[i] == '?' ) throw new FormatException();
                var inner = Seq(p, ref i, n);
                if (i >= p.Length || p[i] != ')') throw new FormatException();
                i++;
                return _ => inner;
            case '[':
                var set = Class(p, ref i);
                return k => set[(n + k) % set.Count].ToString();
            case '\\':
                var e = p[i++];
                var esc = Escape(e) ?? [e];
                return k => esc[(n + k) % esc.Count].ToString();
            case '.':
                return k => ((char)('a' + (n + k) % 26)).ToString();
            case '*' or '+' or '?' or '{' or '^' or '$':
                throw new FormatException();
            default:
                return _ => c.ToString();
        }
    }

    static List<char>? Escape(char e) => e switch
    {
        'd' => Range('0', '9'),
        'w' => [.. Range('a', 'z'), .. Range('0', '9')],
        's' => [' '],
        'n' => ['\n'],
        't' => ['\t'],
        'D' or 'W' or 'S' or 'p' or 'P' or 'i' or 'c' or 'I' or 'C' => throw new FormatException(),
        _ => null,
    };

    static List<char> Range(char a, char b) => Enumerable.Range(a, b - a + 1).Select(x => (char)x).ToList();

    static List<char> Class(string p, ref int i)
    {
        if (p[i] == '^') throw new FormatException();
        var set = new List<char>();
        while (p[i] != ']')
        {
            char ch;
            if (p[i] == '\\') { i++; var e = Escape(p[i]); i++; if (e is not null) { set.AddRange(e); continue; } ch = p[i - 1]; }
            else ch = p[i++];
            if (p[i] == '-' && p[i + 1] != ']') { i++; var hi = p[i] == '\\' ? p[++i] : p[i]; i++; set.AddRange(Range(ch, hi)); }
            else set.Add(ch);
        }
        i++;
        return set.Count == 0 ? throw new FormatException() : set;
    }

    static (int min, int max) Quantifier(string p, ref int i)
    {
        if (i >= p.Length) return (1, 1);
        switch (p[i])
        {
            case '?': i++; return (0, 1);
            case '*': i++; return (0, int.MaxValue);
            case '+': i++; return (1, int.MaxValue);
            case '{':
                var end = p.IndexOf('}', i);
                var parts = p[(i + 1)..end].Split(',');
                i = end + 1;
                var min = int.Parse(parts[0]);
                var max = parts.Length == 1 ? min : parts[1].Length == 0 ? int.MaxValue : int.Parse(parts[1]);
                return (min, max);
            default: return (1, 1);
        }
    }
}

/// <summary>Reads rows and run values out of an exemplar XML, matching by local names (namespace-agnostic).</summary>
internal static class ExemplarValues
{
    /// <summary>The kind's element in the exemplar (the root itself, or the choice branch under it); null when it is another kind.</summary>
    static XElement? KindElement(SchemaKind kind, XDocument xml) => xml.Root is not { } root ? null
        : kind.RootWrapper is null ? (root.Name.LocalName == kind.Name ? root : null)
        : (root.Name.LocalName == kind.RootWrapper ? Child(root, kind.Name) : null);

    public static bool Contains(SchemaKind kind, XDocument xml) => KindElement(kind, xml) is not null;

    /// <summary>
    /// One row per deepest repeating instance (path -> value), holding only values present in the exemplar. An instance with
    /// no children at the next repeating level still yields a row, so its own values are not lost.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(SchemaKind kind, IReadOnlyList<string> chain, XDocument xml)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (KindElement(kind, xml) is { } kindEl) Emit(0, kindEl, kind.Name, new Dictionary<string, string>());
        return rows;

        void Emit(int level, XElement ctx, string ctxPath, Dictionary<string, string> acc)
        {
            foreach (var t in kind.Targets.Where(t => t.RepeatingAncestors.Count == level))
                if (Value(ctx, Rel(ctxPath, t.Path)) is { } v) acc[t.Path] = v;
            var instances = level == chain.Count ? [] : Instances(ctx, Rel(ctxPath, chain[level])).ToList();
            if (instances.Count == 0) { if (level > 0 || chain.Count == 0) rows.Add(acc); return; }
            foreach (var inst in instances)
                Emit(level + 1, inst, chain[level], new Dictionary<string, string>(acc));
        }
    }

    /// <summary>Document-level values (root target path -> value) present in the exemplar.</summary>
    public static IReadOnlyDictionary<string, string> RootValues(SchemaDocument doc, XDocument xml) =>
        xml.Root is { } r && r.Name.LocalName == doc.RootName
            ? doc.RootTargets.Select(t => (t.Path, v: Value(r, Rel(doc.RootName, t.Path)))).Where(x => x.v is not null).ToDictionary(x => x.Path, x => x.v!)
            : new Dictionary<string, string>();

    static string[] Rel(string basePath, string path) => path.Split('/').Skip(basePath.Split('/').Length).ToArray();

    static XElement? Child(XElement e, string local) => e.Elements().FirstOrDefault(c => c.Name.LocalName == local);

    static IEnumerable<XElement> Instances(XElement ctx, string[] rel)
    {
        var e = ctx;
        foreach (var seg in rel[..^1]) { e = Child(e, seg); if (e is null) return []; }
        return e.Elements().Where(c => c.Name.LocalName == rel[^1]);
    }

    static string? Value(XElement ctx, string[] rel)
    {
        var e = ctx;
        foreach (var seg in rel)
        {
            if (seg.StartsWith('@')) return e.Attributes().FirstOrDefault(a => a.Name.LocalName == seg[1..])?.Value;
            e = Child(e, seg);
            if (e is null) return null;
        }
        return e.Value;
    }
}
