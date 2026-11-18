using Csv2Xml.Runtime.Run;

namespace Csv2Xml.TestPlugin;

/// <summary>Test-only plugin. The SubmitterName run value selects a failure mode: "throw" or "duplicate"; anything else is a no-op.</summary>
public static class MisbehavingMiddleware
{
    public static void Transform(Context ctx)
    {
        switch (ctx.Parameters.GetValueOrDefault("SubmitterName"))
        {
            case "throw":
                throw new InvalidOperationException("boom");
            case "duplicate":
                ctx.Files.Add(PackageFile.FromText(ctx.Files[0].Name.ToUpperInvariant(), "clash")); // zip names compare case-insensitively
                break;
        }
    }
}
