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
            RootFields: RootFields(doc),
            RootNamespace: kind.RootWrapper is null ? null : doc.Namespace,
            LocalNamespace: doc.LocalNamespace ?? doc.Namespace,
            Encoding: encoding);
    }

    public static IReadOnlyList<RootFieldSpec> RootFields(SchemaDocument doc) =>
        doc.RootTargets.Select(t => new RootFieldSpec(t.Path, t.Required, t.DataType)).ToList();

    /// <summary>
    /// Run-parameter definitions for document-level values, from the root fields a mapping records (so no XSD is needed).
    /// Key = item name ("submissionId" for "Submission/@submissionId"); date-times default to {now}, dates to {today}.
    /// The host may edit labels, defaults and requiredness before storing the job.
    /// </summary>
    public static IReadOnlyList<RunParameterDef> Parameters(IEnumerable<RootFieldSpec> rootFields) =>
        rootFields.DistinctBy(f => f.Path).Select(f =>
        {
            var name = f.Path.Split('/')[^1].TrimStart('@');
            return new RunParameterDef(name, f.Path, Humanise(name), f.DataType, f.Required,
                f.DataType switch { "datetime" => "{now}", "date" => "{today}", _ => null });
        }).ToList();

    public static JobSpec BuildJob(string name, IEnumerable<MappingSpec> mappings)
    {
        var list = mappings.ToList();
        return new(name, Parameters(list.SelectMany(m => m.RootFields ?? [])), list);
    }

    static string Humanise(string name) =>
        string.Join(' ', NameMatcher.Tokens(name)) is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : name;
}
