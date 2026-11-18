using System.Text;
using System.Xml;
using Csv2Xml.Design.Matching;
using Csv2Xml.Runtime.Csv;
using Csv2Xml.Design.Samples;
using Csv2Xml.Design.Schema;
using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Run;
using Csv2Xml.Runtime.Validation;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Design;

/// <summary>A CSV column as offered to a user: zero-based index plus an unambiguous display name (display only, never stored).</summary>
public sealed record ColumnOption(int Index, string DisplayName);

/// <summary>Outcome of Build: Spec is null when Errors is non-empty. Warnings never block.</summary>
public sealed record BuildResult(MappingSpec? Spec, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Success => Spec is not null;
}

/// <summary>Outcome of Verify: the generated XML and every mapping, row or XSD problem. Valid when Problems is empty.</summary>
public sealed record VerifyResult(string Xml, IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Headless facade for designing mappings, used identically by a host application and the test UI:
/// read the XSD, list a kind's XML targets and the CSV's columns, take suggestions, submit every assignment
/// (target path -> column index) at once, verify against a sample, and assemble a job. Pure functions; no I/O.
/// </summary>
public static class MappingDesigner
{
    public static SchemaDocument LoadSchema(string xsdText) => SchemaReader.ReadDocumentText(xsdText);

    public static IReadOnlyList<string> Kinds(SchemaDocument doc) => doc.Kinds.Select(k => k.Name).ToList();

    /// <summary>Kinds ranked by how well the headers fit them, best first.</summary>
    public static IReadOnlyList<(string Kind, double Score)> RankKinds(SchemaDocument doc, IReadOnlyList<string> headers) =>
        MappingSuggester.Classify(headers, doc.Kinds).Select(m => (m.Kind.Name, m.Score)).ToList();

    /// <summary>Mappable targets of a kind, in document order. Document-level run values are excluded.</summary>
    public static IReadOnlyList<SchemaTarget> Targets(SchemaDocument doc, string kind) => Kind(doc, kind).Targets;

    /// <summary>
    /// The header row of a CSV, read exactly as the runtime will read it (same parser, BOM handling and strict decoding),
    /// so recorded header text matches at run time. Use this rather than splitting the first line yourself.
    /// </summary>
    /// <exception cref="InvalidDataException">Empty input, malformed CSV, or bytes invalid in the encoding.</exception>
    public static IReadOnlyList<string> ReadHeaders(Stream csv, char delimiter = ',', string? encoding = null)
    {
        using var reader = CsvParser.Open(csv, encoding);
        try { return CsvParser.Read(reader, delimiter).FirstOrDefault() ?? throw new InvalidDataException("CSV is empty"); }
        catch (Exception e) when (CsvParser.DescribeInputError(e) is { } msg) { throw new InvalidDataException(msg, e); }
    }

    /// <summary>
    /// Guesses the delimiter from a header line: the most frequent of , ; tab | outside quotes (',' when none occur).
    /// A guess: show it to the user to confirm.
    /// </summary>
    public static char DetectDelimiter(string headerLine)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        var quoted = false;
        foreach (var c in headerLine)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.MaxBy(kv => kv.Value);
        return best.Value == 0 ? ',' : best.Key;
    }

    /// <summary>Header text, suffixed " (#n)" (1-based) where it is blank or would otherwise be ambiguous (trim, case-insensitive).</summary>
    public static IReadOnlyList<ColumnOption> Columns(IReadOnlyList<string> headers)
    {
        var display = headers.Select(h => string.IsNullOrWhiteSpace(h) ? "(blank)" : h.Trim()).ToArray();
        var suffixed = new bool[display.Length];
        for (var i = 0; i < display.Length; i++) if (string.IsNullOrWhiteSpace(headers[i])) Suffix(i);
        while (true)
        {
            var clash = display.Select((d, i) => (d, i)).GroupBy(x => x.d, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).SelectMany(g => g).Where(x => !suffixed[x.i]).Select(x => x.i).ToList();
            if (clash.Count == 0) break;
            clash.ForEach(Suffix);
        }
        return display.Select((d, i) => new ColumnOption(i, d)).ToList();

        void Suffix(int i) { display[i] += $" (#{i + 1})"; suffixed[i] = true; }
    }

    /// <summary>Suggested column per target path (null = no confident match). One column per target, one target per column.</summary>
    public static IReadOnlyDictionary<string, int?> Suggest(SchemaDocument doc, string kind, IReadOnlyList<string> headers) =>
        MappingSuggester.Suggest(headers, Kind(doc, kind)).ToDictionary(s => s.Target.Path, s => s.ColumnIndex);

    /// <summary>
    /// Validates the complete set of assignments and builds the spec. Errors: unknown kind/path, run-value path, column out of
    /// range, required target unassigned. Warning: one column feeding several targets. The header text at each assigned
    /// index is recorded so the runtime can detect a changed CSV layout; the XSD's hash is recorded to detect a changed XSD.
    /// </summary>
    /// <param name="encoding">CSV encoding name recorded in the spec (null = UTF-8), e.g. "windows-1252" for Excel exports on Western Windows.</param>
    public static BuildResult Build(SchemaDocument doc, string kind, IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, int> assignments, char delimiter = ',', string? encoding = null)
    {
        var k = doc.Kinds.FirstOrDefault(x => x.Name == kind);
        if (k is null) return new BuildResult(null, [$"unknown kind '{kind}'"], []);

        var known = k.Targets.Select(t => t.Path).ToHashSet();
        var rootPaths = doc.RootTargets.Select(t => t.Path).ToHashSet();
        var inRange = (int c) => c >= 0 && c < headers.Count;

        var errors = assignments.Keys.Where(p => !known.Contains(p))
            .Select(p => rootPaths.Contains(p) ? $"'{p}' is a run value supplied per run, not a CSV column" : $"unknown target '{p}'")
            .Concat(assignments.Where(a => known.Contains(a.Key) && !inRange(a.Value))
                .Select(a => $"'{a.Key}' assigned column {a.Value + 1}, but the CSV has {headers.Count} columns"))
            .Concat(k.Targets.Where(t => t.Required && !assignments.ContainsKey(t.Path))
                .Select(t => $"required '{t.Path}' has no column"))
            .ToList();
        if (RepeatChain.FirstBranch(k.Targets.Where(t => assignments.ContainsKey(t.Path)).Select(t => (t.Path, t.RepeatingAncestors))) is { } branch)
            errors.Add($"'{branch}' is in a different repeating group from other assigned values; one CSV row cannot express both (leave one group unassigned)");

        var cols = Columns(headers);
        var warnings = assignments.Where(a => known.Contains(a.Key) && inRange(a.Value))
            .GroupBy(a => a.Value).Where(g => g.Count() > 1).OrderBy(g => g.Key)
            .Select(g => $"column '{cols[g.Key].DisplayName}' feeds {g.Count()} targets: {string.Join(", ", g.Select(a => a.Key).Order())}")
            .ToList();

        return errors.Count > 0
            ? new BuildResult(null, errors, warnings)
            : new BuildResult(SpecBuilder.Build(doc, k, assignments, headers, delimiter, encoding), [], warnings);
    }

    /// <summary>Why a CSV with these headers cannot be run with the spec (moved/missing columns, unmapped required targets); empty when it can.</summary>
    public static IReadOnlyList<string> LayoutProblems(MappingSpec spec, IReadOnlyList<string> headers)
    {
        try { ColumnBinder.Bind(spec, headers.ToArray()); return []; }
        catch (MappingException e) { return e.Problems; }
    }

    /// <summary>
    /// Design-time proof: transforms a sample CSV with the spec and validates the XML against the XSD text. Run values not
    /// supplied are synthesised from their types when required (optional ones are omitted, as at run time). Never throws for bad mappings or data; reports them as Problems.
    /// <paramref name="omitXmlDeclaration"/> mirrors <see cref="JobSpec.OmitXmlDeclaration"/> so the preview matches production output.
    /// </summary>
    public static VerifyResult Verify(string xsdText, MappingSpec spec, string sampleCsv, IReadOnlyDictionary<string, string>? runValues = null,
        bool omitXmlDeclaration = true)
    {
        if (spec.XsdHash is not null && !string.Equals(spec.XsdHash, XsdValidator.Hash(xsdText), StringComparison.OrdinalIgnoreCase))
            return new VerifyResult("", ["mapping was designed against a different XSD; rebuild the mapping"]);

        // As at run time: supplied values are written; required ones not supplied get a placeholder; optional ones are omitted.
        var values = (spec.RootFields ?? [])
            .Select(f => (f.Path, Value: runValues?.GetValueOrDefault(f.Path) is { Length: > 0 } v ? v
                : f.Required ? SyntheticValues.For(new SchemaTarget(f.Path, f.Path.Split('/')[^1], TargetKind.Element, f.DataType, true, []), 1).Text
                : null))
            .Where(x => x.Value is not null)
            .ToDictionary(x => x.Path, x => x.Value!);

        var sw = new Utf8StringWriter();
        TransformResult res;
        try
        {
            using var xw = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = omitXmlDeclaration });
            res = new XmlTransformer().Transform(spec, new StringReader(sampleCsv), xw, rootValues: values);
        }
        catch (MappingException e) { return new VerifyResult("", e.Problems); }
        catch (NotSupportedException e) { return new VerifyResult("", [e.Message]); }

        var xml = sw.ToString();
        var problems = res.Errors.Select(e => $"row {e.Row}: {e.Message}").ToList();
        if (!res.Halted)
            problems.AddRange(XsdValidator.Validate(xml, XsdValidator.Compile(xsdText)).Issues.Select(i => $"XSD {i.Line}:{i.Column}: {i.Message}"));
        return new VerifyResult(xml, problems);
    }

    /// <summary>A runnable job from one mapping per kind, with run parameters defaulted from the XSD's document-level values.</summary>
    public static JobSpec Job(SchemaDocument doc, string name, IEnumerable<MappingSpec> mappings) => SpecBuilder.BuildJob(name, doc, mappings);

    static SchemaKind Kind(SchemaDocument doc, string kind) =>
        doc.Kinds.FirstOrDefault(k => k.Name == kind) ?? throw new ArgumentException($"unknown kind '{kind}'", nameof(kind));

    /// <summary>Declares utf-8 in the XML header, matching how the text is saved (a plain StringWriter declares utf-16).</summary>
    sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
