using System.Text;

namespace Csv2Xml.Runtime.Run;

public sealed record InputReport(
    string Kind, string InputFile, long InputBytes, long DataRows, long RowsWritten,
    IReadOnlyList<string> RowErrors, string OutputFile, long OutputBytes, long BlankRows = 0);

/// <summary>Structured record of a run; plugins may read it and adjust it before Render().</summary>
public sealed class RunReport
{
    public string JobName { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public string? Plugin { get; set; }
    public Dictionary<string, string> Parameters { get; } = [];
    public List<InputReport> Inputs { get; } = [];
    public List<string> Notes { get; } = [];

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Job: {JobName}");
        sb.AppendLine($"Started:  {Started:O}");
        sb.AppendLine($"Finished: {Finished:O}");
        if (Plugin is not null) sb.AppendLine($"Plugin:   {Plugin}");
        sb.AppendLine().AppendLine("Run values:");
        foreach (var (k, v) in Parameters) sb.AppendLine($"  {k} = {v}");
        sb.AppendLine().AppendLine("Inputs:");
        foreach (var i in Inputs)
        {
            sb.AppendLine($"  [{i.Kind}] {i.InputFile} ({i.InputBytes:N0} bytes) -> {i.OutputFile} ({i.OutputBytes:N0} bytes)");
            sb.AppendLine($"      data rows: {i.DataRows:N0}, written: {i.RowsWritten:N0}, row errors: {i.RowErrors.Count:N0}, blank rows skipped: {i.BlankRows:N0}");
            foreach (var e in i.RowErrors.Take(20)) sb.AppendLine($"      ! {e}");
        }
        if (Notes.Count > 0) { sb.AppendLine().AppendLine("Notes:"); foreach (var n in Notes) sb.AppendLine("  " + n); }
        return sb.ToString();
    }
}
