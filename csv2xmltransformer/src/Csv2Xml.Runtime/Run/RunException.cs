namespace Csv2Xml.Runtime.Run;

/// <summary>A run was refused or stopped. Problems lists every reason found (all inputs are checked before stopping).</summary>
public sealed class RunException(IReadOnlyList<string> problems) : Exception(string.Join("; ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
