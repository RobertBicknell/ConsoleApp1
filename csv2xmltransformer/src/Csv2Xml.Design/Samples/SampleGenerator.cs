using System.Text;
using System.Xml.Linq;
using Csv2Xml.Design.Matching;
using Csv2Xml.Design.Schema;
using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Design.Samples;

public enum Expectation { Valid, LayoutError, RowError }

/// <summary>One generated CSV with the mapping that fits it, what should happen, and what did happen when it was run.</summary>
/// <param name="SuggestedCorrectly">Assigned targets that MappingDesigner.Suggest gets right unaided (of Assigned).</param>
public sealed record SampleFile(
    string Kind, string Scenario, string Description, Expectation Expected,
    string Csv, MappingSpec Mapping, VerifyResult Verification, int SuggestedCorrectly, int Assigned)
{
    public string CsvFileName => $"{Kind}.{Scenario}.csv";
    public string MappingFileName => $"{Kind}.{Scenario}.mapping.json";
    public string XmlFileName => $"{Kind}.{Scenario}.expected.xml";

    /// <summary>The run behaved as Expected: valid output, or failure of the expected sort.</summary>
    public bool Passed => Expected switch
    {
        Expectation.Valid => Verification.IsValid,
        Expectation.LayoutError => Verification.Problems.Any(p => p.Contains("expected header")),
        Expectation.RowError => Verification.Problems.Any(p => p.StartsWith("row ")),
        _ => false,
    };
}

public sealed record SampleSet(IReadOnlyList<SampleFile> Files, IReadOnlyList<string> Notes, IReadOnlyDictionary<string, string> RunValues)
{
    public bool AllPassed => Files.Count > 0 && Files.All(f => f.Passed);

    /// <summary>Every output as file name -> UTF-8 bytes: CSVs, mappings, expected XML for valid scenarios, README.md.</summary>
    public IReadOnlyDictionary<string, byte[]> ToFiles()
    {
        var d = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["README.md"] = Encoding.UTF8.GetBytes(Readme()) };
        foreach (var f in Files)
        {
            d[f.CsvFileName] = Encoding.UTF8.GetBytes(f.Csv);
            d[f.MappingFileName] = ConfigJson.ToBytes(f.Mapping);
            if (f.Expected == Expectation.Valid && f.Verification.Xml.Length > 0) d[f.XmlFileName] = Encoding.UTF8.GetBytes(f.Verification.Xml);
        }
        return d;
    }

    public void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, bytes) in ToFiles()) File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }

    public string Readme()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated sample CSVs").AppendLine();
        sb.AppendLine($"Result: {(AllPassed ? "all files behaved as expected" : $"{Files.Count(f => !f.Passed)} of {Files.Count} files did NOT behave as expected")}.").AppendLine();
        sb.AppendLine("Each `X.csv` has `X.mapping.json` (the correct mapping) and, when valid, `X.expected.xml` (the output it produces).").AppendLine();
        sb.AppendLine("| File | Expected | Result | Suggestions | Demonstrates |").AppendLine("|---|---|---|---|---|");
        foreach (var f in Files)
            sb.AppendLine($"| {f.CsvFileName} | {f.Expected} | {(f.Passed ? "PASS" : "FAIL")} | {f.SuggestedCorrectly}/{f.Assigned} | {f.Description} |");
        if (RunValues.Count > 0)
        {
            sb.AppendLine().AppendLine("## Run values used").AppendLine();
            foreach (var (k, v) in RunValues) sb.AppendLine($"- `{k}` = `{v}`");
        }
        if (Notes.Count > 0)
        {
            sb.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var n in Notes) sb.AppendLine("- " + n);
        }
        var failed = Files.Where(f => !f.Passed).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine().AppendLine("## Failures").AppendLine();
            foreach (var f in failed)
            {
                sb.AppendLine($"### {f.CsvFileName}");
                foreach (var p in f.Verification.Problems.Take(10)) sb.AppendLine("- " + p);
                if (f.Verification.Problems.Count == 0) sb.AppendLine("- expected a failure but the file was valid");
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Generates, per document kind, CSV files demonstrating each mapping ability, plus the correct mapping for each, and
/// proves each one by running it through the real writer and XSD validation. Values come from the exemplar XML when it
/// contains the kind, otherwise they are synthesised from the XSD types and restrictions.
/// </summary>
public static class SampleGenerator
{
    sealed record Col(string Header, string? Path, string Filler = "");

    sealed record Table(IReadOnlyList<Col> Cols, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

    /// <exception cref="InvalidDataException">The XSD is unusable (see MappingDesigner.LoadSchema) or the exemplar is not well-formed XML.</exception>
    public static SampleSet Generate(string xsdText, string? exemplarXml = null)
    {
        var doc = MappingDesigner.LoadSchema(xsdText);
        XDocument? xml;
        try { xml = exemplarXml is null ? null : XDocument.Parse(exemplarXml); }
        catch (System.Xml.XmlException e) { throw new InvalidDataException("exemplar XML is not well-formed: " + e.Message, e); }
        var notes = new List<string>();

        var exemplarRoot = xml is null ? new Dictionary<string, string>() : ExemplarValues.RootValues(doc, xml);
        var runValues = doc.RootTargets.ToDictionary(t => t.Path, t => exemplarRoot.TryGetValue(t.Path, out var v) ? v : SyntheticValues.For(t, 1).Text);

        var files = new List<SampleFile>();
        foreach (var kind in doc.Kinds)
        {
            var chain = RepeatChain.Of(kind.Targets.Select(t => t.RepeatingAncestors));
            var offChain = kind.Targets.Where(t => !t.RepeatingAncestors.SequenceEqual(chain.Take(t.RepeatingAncestors.Count))).ToList();
            if (offChain.FirstOrDefault(t => t.Required) is { } req)
            {
                notes.Add($"{kind.Name}: skipped. Required '{req.Path}' repeats independently of '{string.Join(" > ", chain)}'; one CSV cannot express sibling repeating groups.");
                continue;
            }
            if (offChain.Count > 0)
                notes.Add($"{kind.Name}: optional {string.Join(", ", offChain.Select(t => $"'{t.Path}'"))} not demonstrated: in a repeating group beside '{string.Join(" > ", chain)}', so it cannot be mapped together with it.");
            var usable = kind with { Targets = kind.Targets.Except(offChain).ToList() };
            if (usable.Targets.Count == 0) { notes.Add($"{kind.Name}: skipped, no mappable values."); continue; }
            files.AddRange(ForKind(doc, xsdText, usable, chain, xml, runValues, notes));
        }
        return new SampleSet(files, notes, runValues);
    }

    static IEnumerable<SampleFile> ForKind(SchemaDocument doc, string xsd, SchemaKind kind, IReadOnlyList<string> chain,
        XDocument? xml, IReadOnlyDictionary<string, string> runValues, List<string> notes)
    {
        var targets = kind.Targets;
        var headers = CleanHeaders(targets);
        var cleanCols = targets.Select(t => new Col(headers[t.Path], t.Path)).ToList();

        var exemplarRows = xml is null ? null : ExemplarValues.Rows(kind, chain, xml);
        var fromExemplar = exemplarRows is { Count: > 0 };
        if (xml is not null && !fromExemplar) notes.Add($"{kind.Name}: not in the exemplar XML; values synthesised from the XSD.");

        var baseRows = fromExemplar ? exemplarRows! : Synthetic(kind, chain, chain.Count == 0 ? [] : [2, .. Enumerable.Repeat(1, chain.Count - 1)], notes);
        var source = fromExemplar ? "exemplar values" : "synthesised values";
        var clean = new Table(cleanCols, baseRows);

        SampleFile Make(string scenario, string description, Table t, Expectation exp = Expectation.Valid, Table? mappedFrom = null)
        {
            var mapTable = mappedFrom ?? t;
            var hdr = mapTable.Cols.Select(c => c.Header).ToList();
            var assign = targets.Select(x => (x.Path, i: IndexOf(mapTable.Cols, x.Path))).Where(x => x.i >= 0).ToDictionary(x => x.Path, x => x.i);
            var build = MappingDesigner.Build(doc, kind.Name, hdr, assign);
            if (!build.Success) throw new InvalidOperationException($"{kind.Name}.{scenario}: generated mapping invalid: {string.Join("; ", build.Errors)}");
            var csv = Render(t);
            var suggested = MappingDesigner.Suggest(doc, kind.Name, t.Cols.Select(c => c.Header).ToList());
            var tAssign = targets.Select(x => (x.Path, i: IndexOf(t.Cols, x.Path))).Where(x => x.i >= 0).ToList();
            var correct = tAssign.Count(a => suggested.GetValueOrDefault(a.Path) == a.i);
            var verify = MappingDesigner.Verify(xsd, build.Spec!, csv, runValues);
            return new SampleFile(kind.Name, scenario, description, exp, csv, build.Spec!, verify, correct, tAssign.Count);
        }

        yield return Make("clean", $"Headers named after the XML items, every column mapped ({source}).", clean);

        var noisyCols = cleanCols.AsEnumerable().Reverse().Select((c, i) => c with { Header = Noisy(c.Header, i) }).ToList();
        noisyCols.Insert(noisyCols.Count / 2, new Col("Notes", null, "not mapped"));
        yield return Make("noisy", "Columns in reverse order, header case/spacing varied, plus an unmapped extra column.", clean with { Cols = noisyCols });

        if (chain.Count >= 2 && !fromExemplar)
        {
            var rows = Synthetic(kind, chain, [2, .. Enumerable.Repeat(2, chain.Count - 1)], notes: null);
            yield return Make("hierarchy", $"Several rows per {Leaf(chain[0])}: consecutive rows with equal {Leaf(chain[0])} values become one element with many {Leaf(chain[^1])} children.", clean with { Rows = rows });
        }

        var optional = targets.Where(t => !t.Required).Select(t => t.Path).ToHashSet();
        if (optional.Count > 0)
        {
            var blanked = baseRows.Select(r => (IReadOnlyDictionary<string, string>)r.Where(kv => !optional.Contains(kv.Key)).ToDictionary()).ToList();
            yield return Make("optional_empty", "Optional columns present but empty: their elements/attributes are omitted.", clean with { Rows = blanked });
        }

        var dupTarget = targets.FirstOrDefault(t => t.Required) ?? targets[0];
        var dupIdx = IndexOf(cleanCols, dupTarget.Path);
        var dupCols = cleanCols.ToList();
        dupCols.Insert(dupIdx, new Col(cleanCols[dupIdx].Header, null, "decoy"));
        yield return Make("duplicate_headers",
            $"Two columns headed '{cleanCols[dupIdx].Header}' (#{dupIdx + 1} decoy, #{dupIdx + 2} real). Suggestion takes the first; the mapping assigns #{dupIdx + 2}.",
            clean with { Cols = dupCols });

        var abbr = targets.Select(t => (t, h: Abbreviate(t.Name)))
            .FirstOrDefault(x => x.h is not null && MappingDesigner.Suggest(doc, kind.Name,
                cleanCols.Select(c => c.Path == x.t.Path ? x.h! : c.Header).ToList())[x.t.Path] != IndexOf(cleanCols, x.t.Path));
        if (abbr.t is not null)
            yield return Make("abbreviated", $"Header '{abbr.h}' for {abbr.t.Path} is not recognised by suggestion and must be assigned manually.",
                clean with { Cols = cleanCols.Select(c => c.Path == abbr.t.Path ? c with { Header = abbr.h! } : c).ToList() });

        if (cleanCols.Count >= 2)
            yield return Make("reordered", "Same headers as the clean file but columns rotated; the clean mapping must refuse it with a layout error.",
                clean with { Cols = [.. cleanCols.Skip(1), cleanCols[0]] }, Expectation.LayoutError, mappedFrom: clean);

        var req = targets.LastOrDefault(t => t.Required);
        if (req is not null)
        {
            var bad = baseRows.Select((r, i) => i == Math.Min(1, baseRows.Count - 1)
                ? (IReadOnlyDictionary<string, string>)r.Where(kv => kv.Key != req.Path).ToDictionary() : r).ToList();
            yield return Make("bad_data", $"Required '{req.Path}' is empty in data row {Math.Min(2, baseRows.Count)}: the run must report the row.",
                clean with { Rows = bad }, Expectation.RowError);
        }
    }

    /// <summary>Rows for a repeat chain with the given instance counts per level; each instance gets a run-wide ordinal.</summary>
    static IReadOnlyList<IReadOnlyDictionary<string, string>> Synthetic(SchemaKind kind, IReadOnlyList<string> chain, int[] counts, List<string>? notes)
    {
        var ordinals = new int[chain.Count + 1];
        var rows = new List<IReadOnlyDictionary<string, string>>();
        var uncertain = new HashSet<string>();

        void Emit(int level, Dictionary<string, string> acc)
        {
            var n = ++ordinals[level];
            foreach (var t in kind.Targets.Where(t => t.RepeatingAncestors.Count == level))
            {
                var v = SyntheticValues.For(t, n);
                if (!v.Certain) uncertain.Add(t.Path);
                acc[t.Path] = v.Text;
            }
            if (level == chain.Count) { rows.Add(acc); return; }
            for (var k = 0; k < counts[level]; k++) Emit(level + 1, new Dictionary<string, string>(acc));
        }

        Emit(0, []);
        foreach (var p in uncertain.Order())
            notes?.Add($"{kind.Name}: could not synthesise a value certain to satisfy the pattern on '{p}'; supply an exemplar XML containing it.");
        return rows;
    }

    static int IndexOf(IReadOnlyList<Col> cols, string path)
    {
        for (var i = 0; i < cols.Count; i++) if (cols[i].Path == path) return i;
        return -1;
    }

    /// <summary>snake_case leaf names; colliding names are qualified with ancestors until unique.</summary>
    static Dictionary<string, string> CleanHeaders(IReadOnlyList<SchemaTarget> targets)
    {
        string Name(SchemaTarget t, int depth) =>
            string.Join("_", t.Segments.TakeLast(depth).SelectMany(s => NameMatcher.Tokens(s.TrimStart('@'))));
        var depthOf = targets.ToDictionary(t => t.Path, _ => 1);
        for (var pass = 0; pass < 8; pass++)
        {
            var clashes = targets.GroupBy(t => Name(t, depthOf[t.Path])).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            if (clashes.Count == 0) break;
            foreach (var t in clashes) depthOf[t.Path] = Math.Min(depthOf[t.Path] + 1, t.Segments.Count);
        }
        return targets.ToDictionary(t => t.Path, t => Name(t, depthOf[t.Path]));
    }

    static string Noisy(string snake, int i)
    {
        var words = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return (i % 3) switch
        {
            0 => string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w[1..])),
            1 => string.Join("", words).ToUpperInvariant(),
            _ => " " + string.Join("-", words) + " ",
        };
    }

    /// <summary>Initials for multi-word names ("UnitPrice" -> "UP"), else first three letters; null when too short to abbreviate.</summary>
    static string? Abbreviate(string name)
    {
        var tokens = NameMatcher.Tokens(name.TrimStart('@'));
        if (tokens.Count >= 2) return string.Concat(tokens.Select(t => char.ToUpperInvariant(t[0])));
        return tokens.Count == 1 && tokens[0].Length >= 6 ? tokens[0][..3] : null;
    }

    static string Leaf(string path) => path.Split('/')[^1];

    static string Render(Table t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", t.Cols.Select(c => Quote(c.Header))));
        foreach (var r in t.Rows)
            sb.AppendLine(string.Join(",", t.Cols.Select(c => Quote(c.Path is null ? c.Filler : r.GetValueOrDefault(c.Path, "")))));
        return sb.ToString();
    }

    static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 || s != s.Trim() ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
