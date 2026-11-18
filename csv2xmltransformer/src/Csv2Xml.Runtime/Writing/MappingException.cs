namespace Csv2Xml.Runtime.Writing;

/// <summary>A spec cannot be applied to an input (layout changed, required target unmapped, run value missing). Raised before any output.</summary>
public sealed class MappingException(IReadOnlyList<string> problems)
    : Exception("Mapping cannot be applied: " + string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
