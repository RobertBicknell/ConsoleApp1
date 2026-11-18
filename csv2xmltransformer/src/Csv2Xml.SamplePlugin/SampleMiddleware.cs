using Csv2Xml.Runtime.Run;

namespace Csv2Xml.SamplePlugin;

/// <summary>Example middleware: the runtime finds and calls the first public Transform(Context) in the plugin folder.</summary>
public class SampleMiddleware
{
    public void Transform(Context ctx)
    {
        var rows = ctx.Report.Inputs.Sum(i => i.DataRows);
        ctx.ReportText += $"\nSample plugin: {rows} data rows across {ctx.Report.Inputs.Count} inputs.\n";
        ctx.Files.Add(PackageFile.FromText("extra/readme.txt", "added by SampleMiddleware"));
    }
}
