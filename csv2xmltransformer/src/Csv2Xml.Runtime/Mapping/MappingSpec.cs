using System.Text.Json.Serialization;

namespace Csv2Xml.Runtime.Mapping;

/// <summary>
/// The settled, self-contained definition the production writer runs from. It carries everything needed
/// to emit conforming XML (structure, order, requiredness) and knows nothing of how it was produced.
/// </summary>
/// <param name="Kind">Name of the document kind (informational).</param>
/// <param name="RootElement">Optional wrapper element enclosing the kind root (e.g. the XSD's choice root).</param>
/// <param name="Namespace">Namespace of the kind root element.</param>
/// <param name="Fields">In XSD document order. Order is significant: it is the emission order.</param>
/// <param name="XsdHash">XsdValidator.Hash of the XSD text the spec was designed against; a run given different XSD text is refused.</param>
/// <param name="RootFields">Document-level values (outside the kind) supplied per run, written inside RootElement before the kind.</param>
/// <param name="GroupKeys">Optional override: repeating-element path -> field paths identifying an instance. Default: all fields owned by that level.</param>
/// <param name="RootNamespace">Namespace of RootElement (a global element); null = Namespace.</param>
/// <param name="LocalNamespace">Namespace of elements below the kind root and of RootElement's children (empty when the XSD's
/// local elements are unqualified); null = Namespace.</param>
/// <param name="Encoding">CSV text encoding name (e.g. "utf-8", "windows-1252"); null = UTF-8. A byte-order mark overrides it.</param>
public sealed record MappingSpec(
    string Kind,
    string? RootElement,
    string? Namespace,
    bool HasHeader,
    char Delimiter,
    IReadOnlyList<FieldSpec> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GroupKeys = null,
    string? XsdHash = null,
    IReadOnlyList<RootFieldSpec>? RootFields = null,
    string? RootNamespace = null,
    string? LocalNamespace = null,
    string? Encoding = null);

/// <param name="Path">Slash path from the kind root; attributes end in '@name'.</param>
/// <param name="RepeatingAncestors">Paths of repeating elements enclosing this field (incl. itself for a repeating element), outermost first.</param>
/// <param name="Column">Header text found at ColumnIndex at design time; verified at run time when HasHeader (never used to bind).</param>
/// <param name="ColumnIndex">Zero-based column the field binds to; null = unmapped.</param>
public sealed record FieldSpec(
    string Path,
    bool Required,
    string DataType,
    IReadOnlyList<string> RepeatingAncestors,
    string? Column,
    int? ColumnIndex)
{
    [JsonIgnore] public bool IsAttribute => Path.Contains("/@") || Path.StartsWith('@');
}

/// <param name="Path">Slash path starting at RootElement; attributes end in '@name'.</param>
public sealed record RootFieldSpec(string Path, bool Required, string DataType)
{
    [JsonIgnore] public bool IsAttribute => Path.Contains("/@");
}
