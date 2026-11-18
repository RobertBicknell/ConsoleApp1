using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Runtime.Writing;

/// <summary>
/// Resolves each field to a CSV column index (-1 = unmapped optional). Binding is by ColumnIndex only; when the CSV has a
/// header, the header text at that index must still equal the recorded Column (trim, case-insensitive), otherwise the
/// input's layout has changed and nothing is written. Throws listing every problem.
/// </summary>
public static class ColumnBinder
{
    public static int[] Bind(MappingSpec spec, string[]? header)
    {
        if (spec.HasHeader && header is null) throw new MappingException(["CSV has no header row"]);
        var problems = spec.Fields.Select(f => Check(f, spec.HasHeader ? header : null)).OfType<string>().ToList();
        if (problems.Count > 0) throw new MappingException(problems);
        return spec.Fields.Select(f => f.ColumnIndex ?? -1).ToArray();
    }

    static string? Check(FieldSpec f, string[]? header) => (f.ColumnIndex, header) switch
    {
        (null, _) when f.Required => $"required '{f.Path}' is not mapped",
        (null, _) => null,
        ( < 0, _) => $"'{f.Path}' has invalid column index {f.ColumnIndex}",
        (_, null) => null,
        (var i, _) when i >= header.Length => $"'{f.Path}' maps to column {i + 1}, but the CSV has only {header.Length} columns",
        (var i, _) when f.Column is not null && !HeaderEquals(header[i.Value], f.Column) =>
            $"'{f.Path}': column {i + 1} expected header '{f.Column}', found '{header[i.Value]}'",
        _ => null,
    };

    /// <summary>Header identity: case-insensitive (ordinal), ignoring surrounding whitespace (incl. NBSP) and a stray BOM.</summary>
    public static bool HeaderEquals(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    static string Clean(string s) => s.Trim().Trim('﻿').Trim();
}
