using System.Text;
using System.Xml;
using Csv2Xml.Runtime.Csv;
using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Runtime.Writing;

public enum ErrorPolicy { Halt, SkipRow }

public sealed record RowError(long Row, string Message);

/// <param name="RowsRead">Records read including the header row and skipped blank rows.</param>
/// <param name="BlankRows">Rows whose fields were all empty/whitespace; skipped, not errors.</param>
public sealed record TransformResult(long RowsRead, long RowsWritten, IReadOnlyList<RowError> Errors, bool Halted, long BlankRows = 0)
{
    public bool Success => !Halted && Errors.Count == 0;
}

/// <summary>
/// Streams CSV rows into XML via XmlWriter; memory is independent of input size (one row + open-element key stack).
/// Rows sharing the same key at an outer repeating level (and contiguous in the input) collapse into one instance.
/// Owned (non-repeating) content of an instance is emitted when the instance opens, i.e. before any repeating child.
/// <para>
/// Row checks (an error halts or skips the row per ErrorPolicy): field count must equal the header's; required values present;
/// mapped values contain only XML-legal characters. Malformed CSV (unterminated quote) or undecodable bytes halt the run.
/// Mapping problems (layout, unbound required targets, missing run values) throw MappingException before any output.
/// </para>
/// </summary>
public sealed class XmlTransformer
{
    public TransformResult Transform(MappingSpec spec, TextReader csv, XmlWriter xml, ErrorPolicy policy = ErrorPolicy.Halt, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? rootValues = null)
    {
        var rows = CsvParser.Read(csv, spec.Delimiter).GetEnumerator();
        string[]? header = null;
        long rowNo = 0, blank = 0, written = 0;
        var errors = new List<RowError>();

        if (spec.HasHeader)
        {
            try { if (!rows.MoveNext()) throw new MappingException(["CSV is empty; header row expected"]); }
            catch (Exception e) when (CsvParser.DescribeInputError(e) is { } msg) { throw new MappingException([$"header row: {msg}"]); }
            header = rows.Current; rowNo = 1;
        }

        var cols = ColumnBinder.Bind(spec, header); // throws before any output
        var rootFields = spec.RootFields ?? [];
        var rootVals = rootFields.Select(f => rootValues?.GetValueOrDefault(f.Path) ?? "").ToArray();
        var rootProblems = rootFields.Select((f, i) => f.Required && string.IsNullOrWhiteSpace(rootVals[i]) ? $"required run value '{f.Path}' not supplied"
                : IllegalXmlChar(rootVals[i]) is { } bad ? $"run value '{f.Path}' contains {bad}" : null)
            .OfType<string>().ToList();
        if (rootProblems.Count > 0) throw new MappingException(rootProblems);

        var plan = new WritePlan(spec);
        var nsLocal = spec.LocalNamespace ?? spec.Namespace ?? "";
        var nsKind = spec.Namespace ?? "";
        var frames = plan.Levels.Select(_ => new Frame()).ToArray();
        var started = false;
        var mapped = Enumerable.Range(0, spec.Fields.Count).Where(i => cols[i] >= 0).ToArray();

        string V(string[] row, int fieldIdx) => cols[fieldIdx] is var c && c >= 0 && c < row.Length ? row[c] : "";

        xml.WriteStartDocument();
        if (spec.RootElement is not null)
        {
            xml.WriteStartElement(spec.RootElement, spec.RootNamespace ?? nsKind);
            if (rootFields.Count > 0) WriteOwned(xml, RootTemplate.Build(spec.RootElement, rootFields), [], (_, i) => rootVals[i], nsLocal);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { if (!rows.MoveNext()) break; }
            catch (Exception e) when (CsvParser.DescribeInputError(e) is { } msg)
            {
                errors.Add(new RowError(rowNo + 1, msg));
                return Finish(halted: true);
            }
            rowNo++;
            var row = rows.Current;

            if (row.All(string.IsNullOrWhiteSpace)) { blank++; continue; }

            if (RowProblem(row) is { } problem)
            {
                errors.Add(new RowError(rowNo, problem));
                if (policy == ErrorPolicy.Halt) return Finish(halted: true);
                continue;
            }

            if (!started)
            {
                started = true;
                xml.WriteStartElement(plan.RootName, nsKind);
                WriteOwned(xml, plan.RootTemplate, row, V, nsLocal);
            }

            var last = frames.Length - 1;
            var j = 0;
            if (last >= 0)
            {
                for (; j < last; j++)
                {
                    var key = plan.Levels[j].KeyFields.Select(k => V(row, k)).ToArray();
                    if (!frames[j].InstanceOpen || !key.AsSpan().SequenceEqual(frames[j].Key)) break;
                }
                for (var k = last; k > j; k--) Close(frames[k], plan.Levels[k], wrappers: true, xml);
                Close(frames[j], plan.Levels[j], wrappers: false, xml);

                for (var k = j; k <= last; k++)
                {
                    var lvl = plan.Levels[k];
                    var fr = frames[k];
                    if (!fr.WrappersOpen) { foreach (var w in lvl.Wrappers) xml.WriteStartElement(w, nsLocal); fr.WrappersOpen = true; }
                    fr.Key = lvl.KeyFields.Select(x => V(row, x)).ToArray();
                    fr.InstanceOpen = true;
                    xml.WriteStartElement(lvl.Template.Name, nsLocal);
                    WriteOwned(xml, lvl.Template, row, V, nsLocal);
                }
            }
            written++;
        }

        return Finish(halted: false);

        string? RowProblem(string[] row)
        {
            if (header is not null && row.Length != header.Length)
                return $"has {row.Length} fields but the header has {header.Length} (unquoted delimiter in a value, or a missing field?)";
            var missing = mapped.Where(i => spec.Fields[i].Required && string.IsNullOrWhiteSpace(V(row, i))).Select(i => spec.Fields[i].Path).ToList();
            if (missing.Count > 0) return "missing required value(s): " + string.Join(", ", missing);
            foreach (var i in mapped)
                if (IllegalXmlChar(V(row, i)) is { } bad) return $"column {cols[i] + 1} ('{spec.Fields[i].Path}') contains {bad}";
            return null;
        }

        TransformResult Finish(bool halted)
        {
            if (!halted)
            {
                for (var k = frames.Length - 1; k >= 0; k--) Close(frames[k], plan.Levels[k], wrappers: true, xml);
                if (!started) xml.WriteStartElement(plan.RootName, nsKind);
                xml.WriteEndElement();
                if (spec.RootElement is not null) xml.WriteEndElement();
                xml.WriteEndDocument();
            }
            xml.Flush();
            return new TransformResult(rowNo, written, errors, halted, blank);
        }
    }

    /// <summary>Describes the first character XML 1.0 cannot carry (control characters, lone surrogates), or null.</summary>
    static string? IllegalXmlChar(string s)
    {
        var i = s.AsSpan().IndexOfAnyExcept(SearchValuesXml.Legal);
        if (i < 0) return null;
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            return IllegalXmlChar(s[(i + 2)..]); // valid pair: keep scanning after it
        return $"a character not allowed in XML (U+{(int)s[i]:X4})";
    }

    static class SearchValuesXml
    {
        /// <summary>XML 1.0 legal BMP characters excluding surrogates (pairs are checked separately).</summary>
        public static readonly System.Buffers.SearchValues<char> Legal = System.Buffers.SearchValues.Create(
            new[] { '\t', '\n', '\r' }.Concat(Enumerable.Range(0x20, 0xD800 - 0x20).Select(c => (char)c))
                .Concat(Enumerable.Range(0xE000, 0xFFFE - 0xE000).Select(c => (char)c)).ToArray());
    }

    sealed class Frame { public string[] Key = []; public bool InstanceOpen, WrappersOpen; }

    static void Close(Frame f, Level l, bool wrappers, XmlWriter xml)
    {
        if (f.InstanceOpen) { xml.WriteEndElement(); f.InstanceOpen = false; }
        if (wrappers && f.WrappersOpen) { foreach (var _ in l.Wrappers) xml.WriteEndElement(); f.WrappersOpen = false; }
    }

    /// <summary>Writes a node's content (its start tag is already written): attributes, text, then child elements that have a value.</summary>
    static void WriteOwned(XmlWriter xml, Node n, string[] row, Func<string[], int, string> v, string ns)
    {
        foreach (var a in n.Kids.Where(k => k.IsAttribute))
        {
            var val = a.FieldIndex >= 0 ? v(row, a.FieldIndex) : "";
            if (!string.IsNullOrWhiteSpace(val)) xml.WriteAttributeString(a.Name, val);
        }
        if (n.FieldIndex >= 0) { var t = v(row, n.FieldIndex); if (!string.IsNullOrWhiteSpace(t)) xml.WriteString(t); }

        foreach (var e in n.Kids.Where(k => !k.IsAttribute))
        {
            if (!HasValue(e, row, v)) continue;
            xml.WriteStartElement(e.Name, ns);
            WriteOwned(xml, e, row, v, ns);
            xml.WriteEndElement();
        }
    }

    static bool HasValue(Node n, string[] row, Func<string[], int, string> v) =>
        (n.FieldIndex >= 0 && !string.IsNullOrWhiteSpace(v(row, n.FieldIndex))) || n.Kids.Any(k => HasValue(k, row, v));
}
