namespace Csv2Xml.Design.Schema;

public enum TargetKind { Element, Attribute }

/// <summary>A value-bearing slot in the XSD: a simple-content element or an attribute.</summary>
/// <param name="Path">Slash path from the kind root, attributes prefixed '@' (e.g. Customer/Address/City, Customer/@id).</param>
/// <param name="Required">True when the value must exist for every instance of its nearest repeating ancestor.</param>
/// <param name="RepeatingAncestors">Paths of ancestor elements (incl. self for elements) with maxOccurs &gt; 1, outermost first.</param>
public sealed record SchemaTarget(
    string Path,
    string Name,
    TargetKind Kind,
    string DataType,
    bool Required,
    IReadOnlyList<string> RepeatingAncestors,
    Facets? Facets = null)
{
    public IReadOnlyList<string> Segments => Path.Split('/');
}

/// <summary>Value restrictions declared on a target's type (most-derived declaration wins). Bounds are kept as lexical XSD text.</summary>
public sealed record Facets(
    IReadOnlyList<string> Enumeration,
    IReadOnlyList<string> Patterns,
    int? Length = null, int? MinLength = null, int? MaxLength = null,
    string? MinInclusive = null, string? MaxInclusive = null, string? MinExclusive = null, string? MaxExclusive = null,
    int? TotalDigits = null, int? FractionDigits = null, string? Fixed = null)
{
    public static readonly Facets None = new([], []);
}

/// <summary>One of the document kinds the XSD permits.</summary>
/// <param name="RootWrapper">Enclosing global element when kinds are choice branches (written around the kind).</param>
public sealed record SchemaKind(string Name, IReadOnlyList<SchemaTarget> Targets, string? RootWrapper = null, string? Namespace = null);

/// <summary>
/// A whole XSD: the kinds, plus the document-level (wrapper) values that sit outside the kinds.
/// Root targets are never mapped from CSV; they are supplied once per run.
/// </summary>
/// <param name="XsdHash">Content identity of the XSD (XsdValidator.Hash); recorded in every mapping built from this document.</param>
/// <param name="Namespace">Target namespace (that of the global elements).</param>
/// <param name="LocalNamespace">Namespace of local elements: the target namespace when elementFormDefault is qualified, otherwise empty.</param>
public sealed record SchemaDocument(string RootName, string? Namespace, IReadOnlyList<SchemaTarget> RootTargets, IReadOnlyList<SchemaKind> Kinds, string XsdHash,
    string? LocalNamespace = null);
