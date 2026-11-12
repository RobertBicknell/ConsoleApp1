using ToolRunner.Core;

namespace ToolRunner.Tools;

/// The stack's version, and source that matches it. Verbs: detect, available, verify-source, checkout.
/// "The version" everywhere is: the `version` argument, else the variable VERSION, else what `detect` reads from the
/// image tags in the compose file. How versions map to image tags and git refs is set by VERSION_PATTERN,
/// VERSION_GIT_REFS and VERSION_LATEST_REFS.
public sealed class VersionTool : ITool
{
    public string Name => "version";

    private static readonly Arg[] Which = A.Args(
        A.Str("version", "The version (default ${VERSION}, then the one detected from the compose file)."),
        A.Str("composeFile", "Compose file to detect the version from (default ${COMPOSE_FILE})."));

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("detect", "Read the version from each service's image tag; report the stack's version and any service that is on another.", A.Args(
            A.Str("composeFile", "Compose file (default ${COMPOSE_FILE})."),
            A.Bool("requireSame", "Fail when product images are on different versions (default false)."))),
        A.Verb("available", "List the versions that exist: git refs of a source repo and/or registry tags of an image.", A.Args(
            A.Str("repo", "A git working tree."), A.Str("image", "An image whose registry tags to list (needs REGISTRY_API)."),
            A.Bool("fetch", "Run `git fetch --tags` first (default false)."))),
        A.Verb("verify-source", "Check that a source repo's HEAD is the commit of the version.", A.Args(A.Str("repo", "The git working tree.", true)), Which),
        A.Change("checkout", "Check out the version's ref in a source repo. Refuses if tracked files have uncommitted changes.", A.Args(
            A.Str("repo", "The git working tree.", true), A.Bool("fetch", "Run `git fetch --tags` first (default false).")), Which)
    };

    public Task<ToolResult> RunAsync(ToolContext c) => c.Verb switch
    {
        "detect" => Detect(c),
        "available" => Available(c),
        "verify-source" => Verify(c),
        "checkout" => Checkout(c),
        _ => Task.FromResult(ToolResult.Fail($"Unknown verb '{c.Verb}'"))
    };

    private static string? Val(ToolContext c, string arg, string? variable = null) =>
        c.Opt(arg) is { Length: > 0 } a ? a : variable is not null && c.Var(variable) is { Length: > 0 } v ? v : null;

    private static VersionScheme Scheme(ToolContext c) => VersionScheme.From(c.Var);

    private static async Task<StackVersions?> Read(ToolContext c)
    {
        if (Val(c, "composeFile", "COMPOSE_FILE") is not { } file || await c.Host.ReadTextAsync(file, c.Ct) is not { } text) return null;
        var env = new Dictionary<string, string>();
        if (c.Var("ENV_FILE") is { } envFile && await c.Host.ReadTextAsync(envFile, c.Ct) is { } envText)
            foreach (var v in Scanner.EnvFile(envFile, envText, "env")) env[v.Name] = v.Value ?? "";
        return Versions.Summarise(Versions.Read("", ComposeLayout.Services(file, text, env), new Dictionary<string, string> { [file] = text }, Scheme(c)));
    }

    private static async Task<string?> Effective(ToolContext c) => Val(c, "version", "VERSION") ?? (await Read(c))?.Detected;

    private static async Task<ToolResult> Detect(ToolContext c)
    {
        if (await Read(c) is not { } v) return ToolResult.Fail("compose file not found (composeFile / COMPOSE_FILE)");
        foreach (var i in v.Images)
            c.Log($"{(v.Odd.Contains(i) ? "DIFFERS" : v.Product.Contains(i) ? "ok     " : "       ")} {i.Service,-22} {i.Version ?? "-",-12} {i.Image}{(i.Variable is null ? "" : $"   (tag from ${{{i.Variable}}})")}");
        if (v.Detected is null) return ToolResult.Fail("no version found in any image tag (see VERSION_PATTERN)");
        var pinned = c.Var("VERSION");
        if (pinned is { Length: > 0 } && pinned != v.Detected) c.Log($"note: VERSION is set to {pinned}; the stack is on {v.Detected}");
        var outputs = new Dictionary<string, string> { ["VERSION_DETECTED"] = v.Detected };
        if (v.Variable is not null) outputs["VERSION_VARIABLE"] = v.Variable;
        var summary = $"stack version {v.Detected}{(v.Variable is null ? "" : $" (from ${{{v.Variable}}})")}; {v.Odd.Count} of {v.Product.Count} product image(s) on another version";
        return v.Odd.Count > 0 && c.Bool("requireSame", false) ? ToolResult.Fail(summary) : ToolResult.Success(summary, outputs);
    }

    private static async Task<ToolResult> Available(ToolContext c)
    {
        var scheme = Scheme(c);
        var outputs = new Dictionary<string, string>();
        if (Val(c, "repo") is { } repo)
        {
            if (c.Bool("fetch", false)) await c.Exec("git", ["-C", repo, "fetch", "--tags", "--prune"]);
            var git = await Versions.FromGitAsync(c.Host, repo, scheme, c.Ct);
            c.Log($"git {repo}: {git.Count} version(s)");
            foreach (var g in git.Take(40)) c.Log($"  {g.Version,-14} {g.Kind,-6} {g.Ref}");
            outputs["VERSIONS_GIT"] = string.Join("\n", git.Select(g => g.Version));
        }
        if (Val(c, "image") is { } image)
        {
            if (c.Var("REGISTRY_API") is not { Length: > 0 } api) c.Log("registry: REGISTRY_API is not set");
            else
            {
                var path = Versions.RegistryRepo(image, c.Var("REGISTRY_PREFIX"));
                var (tags, note) = await RegistryClient.TagsAsync(api, path, c.Var("REGISTRY_USER"), c.Var("REGISTRY_TOKEN"), c.Ct);
                if (tags is null) c.Log($"registry: {note}");
                else
                {
                    var versions = Versions.FromTags(tags, scheme);
                    c.Log($"registry {path}: {tags.Count} tag(s), {versions.Count} version(s)");
                    foreach (var v in versions.Take(40)) c.Log($"  {v,-14} {string.Join(" ", tags.Where(t => scheme.Of(t) == v).Take(6))}");
                    outputs["VERSIONS_REGISTRY"] = string.Join("\n", versions);
                }
            }
        }
        return outputs.Count == 0 ? ToolResult.Fail("nothing listed: give 'repo' and/or 'image' (with REGISTRY_API set)") : ToolResult.Success("versions listed", outputs);
    }

    private static async Task<ToolResult> Verify(ToolContext c)
    {
        var repo = c.Str("repo");
        if (await Versions.HeadAsync(c.Host, repo, c.Ct) is not { } head) return ToolResult.Fail($"{repo} is not a git working tree; cannot tell which version the source is");
        if (await Effective(c) is not { } version) return ToolResult.Fail("no version: set VERSION, or point COMPOSE_FILE at the stack");
        var scheme = Scheme(c);
        if (await Versions.ResolveAsync(c.Host, repo, version, scheme, c.Ct) is not { } at)
            return ToolResult.Fail($"version {version}: none of these refs exist in {repo}: {string.Join(", ", scheme.RefsFor(version))} (fetch, or adjust VERSION_GIT_REFS)");
        var outputs = new Dictionary<string, string> { ["SOURCE_REF"] = at.Ref, ["SOURCE_COMMIT"] = head };
        return head == at.Commit
            ? ToolResult.Success($"{repo} is on {at.Ref} ({head[..7]}), version {version}", outputs)
            : ToolResult.Fail($"{repo} is on {head[..7]} but version {version} is {at.Ref} ({at.Commit[..7]}): the source does not match the image (run checkout-version)");
    }

    private static async Task<ToolResult> Checkout(ToolContext c)
    {
        var repo = c.Str("repo");
        if (await Effective(c) is not { } version) return ToolResult.Fail("no version: set VERSION, or point COMPOSE_FILE at the stack");
        if (c.Bool("fetch", false) && (await c.Exec("git", ["-C", repo, "fetch", "--tags", "--prune"])).Code != 0) return ToolResult.Fail("git fetch failed");
        var scheme = Scheme(c);
        if (await Versions.ResolveAsync(c.Host, repo, version, scheme, c.Ct) is not { } at)
            return ToolResult.Fail($"version {version}: none of these refs exist in {repo}: {string.Join(", ", scheme.RefsFor(version))} (fetch, or adjust VERSION_GIT_REFS)");
        var dirty = await c.Exec("git", ["-C", repo, "status", "--porcelain", "-uno"], echo: false);
        if (dirty.Code != 0) return ToolResult.Fail($"{repo} is not a git working tree");
        if (dirty.Stdout.Trim().Length > 0) return ToolResult.Fail($"{repo} has uncommitted changes; commit or stash first");
        var r = await c.Exec("git", ["-C", repo, "checkout", at.Ref]);
        return r.Code == 0
            ? ToolResult.Success($"{repo} is now on {at.Ref} ({at.Commit[..7]}), version {version}", new() { ["SOURCE_REF"] = at.Ref, ["SOURCE_COMMIT"] = at.Commit })
            : ToolResult.Fail($"git checkout {at.Ref} exited {r.Code}");
    }
}
