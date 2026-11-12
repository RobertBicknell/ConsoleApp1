using ToolRunner.Core;

namespace ToolRunner.Tools;

/// Checks that the local source equals what a container image was built from. Verb: verify.
/// Strategy is data (per service, in globals.json), because the situation differs per developer:
///   none  - skip (e.g. the image is pulled from a registry and cannot be tied to local source)
///   label - image label org.opencontainers.image.revision == git HEAD  (set it with docker/build revisionFrom)
///   tag   - image tag is a commit-id prefix (>= 7 chars) of git HEAD
/// A dirty working tree fails by default: it cannot equal any built image.
public sealed class SourceTool : ITool
{
    public string Name => "source";
    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("verify", "Check that local source equals what the image was built from.", A.Args(
            A.Enum("strategy", "How the image records its commit: none, label (org.opencontainers.image.revision) or tag (commit-id prefix).", "none", "label", "tag"),
            A.Str("src", "Local git working tree."), A.Str("image", "Image to compare."), A.Bool("allowDirty", "Skip the uncommitted-changes check.")))
    };

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        if (c.Verb != "verify") return ToolResult.Fail($"Unknown verb '{c.Verb}'");
        var strategy = c.Str("strategy");
        if (strategy == "none") return ToolResult.Success("source verification skipped (strategy=none)");

        var src = c.Str("src");
        var image = c.Str("image");

        var head = await GitTool.Head(c, src);
        if (string.IsNullOrEmpty(head)) return ToolResult.Fail($"'{src}' is not a git working tree");

        if (!c.Bool("allowDirty", false))
        {
            var s = await c.Exec("git", new[] { "-C", src, "status", "--porcelain", "-uno" }, echo: false);
            if (!string.IsNullOrWhiteSpace(s.Stdout))
                return ToolResult.Fail("Local source has uncommitted changes, so it cannot equal any built image (allowDirty=true to skip this check)");
        }

        string built;
        switch (strategy)
        {
            case "label":
                var r = await c.Exec("docker", new[] { "image", "inspect", "--format",
                    "{{ index .Config.Labels \"org.opencontainers.image.revision\" }}", image }, echo: false);
                if (r.Code != 0) return ToolResult.Fail($"Image '{image}' not found on this host");
                built = r.Stdout.Trim();
                break;
            case "tag":
                var colon = image.LastIndexOf(':');
                built = colon > image.LastIndexOf('/') ? image[(colon + 1)..] : "";
                break;
            default:
                return ToolResult.Fail($"Unknown strategy '{strategy}' (none | label | tag)");
        }

        if (built.Length < 7)
            return ToolResult.Fail($"Image does not record a usable commit id (strategy={strategy}, found '{built}'). " +
                                   "If it was pulled rather than built locally, it may not record one.");

        return head.StartsWith(built, StringComparison.OrdinalIgnoreCase)
            ? ToolResult.Success($"source == image ({head[..7]})", new() { ["SOURCE_COMMIT"] = head })
            : ToolResult.Fail($"Local HEAD {head[..7]} differs from image commit {built[..Math.Min(7, built.Length)]}");
    }
}
