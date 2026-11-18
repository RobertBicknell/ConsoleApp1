using Csv2Xml.Design.Schema;
using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Run;

namespace Csv2Xml.Design.Matching;

/// <summary>Design-time bridge: turns (kind + chosen column per target) into the runtime's self-contained MappingSpec.</summary>
internal static class SpecBuilder
{
    /// <summary>
    /// Builds the spec. Unassigned targets are kept (unbound) so the spec still describes the whole kind.
    /// With headers, the header text at each assigned index is recorded for run-time layout checks; without, the CSV is headerless.
    /// No validation here: MappingDesigner.Build validates assignments first.
    /// </summary>
    /// <param name="assignment">Target path -> zero-based column index.</param>
    public static MappingSpec Build(SchemaDocument doc, SchemaKind kind, IReadOnlyDictionary<string, int> assignment,
        IReadOnlyList<string>? headers, char delimiter = ',', string? encoding = null)
    {
        var fields = kind.Targets.Select(t =>
        {
            var col = assignment.TryGetValue(t.Path, out var c) ? c : (int?)null;
            return new FieldSpec(t.Path, t.Required, t.DataType, t.RepeatingAncestors, col is { } i ? headers?[i] : null, col);
        }).ToList();
        return new MappingSpec(kind.Name, kind.RootWrapper, kind.Namespace, HasHeader: headers is not null, delimiter, fields,
            XsdHash: doc.XsdHash,
            RootFields: doc.RootTargets.Select(t => new RootFieldSpec(t.Path, t.Required, t.DataType)).ToList(),
            RootNamespace: kind.RootWrapper is null ? null : doc.Namespace,
            LocalNamespace: doc.LocalNamespace ?? doc.Namespace,
            Encoding: encoding);
    }

    /// <summary>Default run-parameter definitions for the document-level targets; the user may edit them (labels, defaults, which are required).</summary>
    public static IReadOnlyList<RunParameterDef> DefaultParameters(IEnumerable<SchemaTarget> rootTargets) =>
        rootTargets.Select(t => new RunParameterDef(t.Name, t.Path, Humanise(t.Name), t.DataType, t.Required,
            t.DataType switch { "datetime" => "{now}", "date" => "{today}", _ => null })).ToList();

    public static JobSpec BuildJob(string name, SchemaDocument doc, IEnumerable<MappingSpec> mappings) =>
        new(name, DefaultParameters(doc.RootTargets), mappings.ToList());

    static string Humanise(string name) =>
        string.Join(' ', NameMatcher.Tokens(name)) is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : name;
}
