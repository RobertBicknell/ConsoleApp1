using System.Xml;
using Csv2Xml.Design.Matching;
using Csv2Xml.Design.Schema;
using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Tests;

public static class TestKit
{
    public static readonly string SamplesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples"));
    public static readonly string XsdPath = Path.Combine(SamplesDir, "dev.xsd");
    public static readonly string XsdText = File.ReadAllText(XsdPath);
    public static readonly SchemaDocument Doc = SchemaReader.ReadDocumentText(XsdText);

    public static SchemaKind Kind(string name) => Doc.Kinds.Single(k => k.Name == name);

    public static readonly IReadOnlyDictionary<string, string> RootValues = new Dictionary<string, string>
    {
        ["Submission/@submissionId"] = "S-1",
        ["Submission/SubmitterName"] = "Tester",
        ["Submission/SubmittedAt"] = "2026-01-01T00:00:00Z",
    };

    /// <summary>Suggested assignments only (path -> column).</summary>
    public static Dictionary<string, int> Suggested(SchemaKind k, IReadOnlyList<string> headers) =>
        MappingSuggester.Suggest(headers, k).Where(s => s.ColumnIndex is not null).ToDictionary(s => s.Target.Path, s => s.ColumnIndex!.Value);

    /// <summary>Spec for a headed CSV, mapped by suggestion.</summary>
    public static MappingSpec Build(SchemaKind k, IReadOnlyList<string> headers) => SpecBuilder.Build(Doc, k, Suggested(k, headers), headers);

    public static TransformResult Transform(MappingSpec spec, TextReader csv, XmlWriter xml, ErrorPolicy p = ErrorPolicy.Halt) =>
        new XmlTransformer().Transform(spec, csv, xml, p, rootValues: RootValues);
}
