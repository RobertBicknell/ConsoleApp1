using ToolRunner.Core;

namespace ToolRunner.Tools;

/// git, run on the selected host. Verbs: head, status, require-clean, diff, pull.
public sealed class GitTool : ITool
{
    public string Name => "git";
    private static readonly Arg[] Dirty_ = A.Args(
        A.Bool("untracked", "Count untracked files as changes (default false)."), A.List("paths", "Limit to these paths."));

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("head", "Commit id of HEAD.", A.Args(A.Str("path", "Working tree (default ${APP_ROOT})."), A.Str("into", "State variable (default GIT_HEAD)."))),
        A.Verb("status", "Report changed files.", A.Args(A.Str("path", "Working tree (default ${APP_ROOT}).")), Dirty_),
        A.Verb("require-clean", "Fail if there are uncommitted changes.", A.Args(A.Str("path", "Working tree (default ${APP_ROOT}).")), Dirty_),
        A.Verb("diff", "Working-tree diff, or compare two files (even outside a repo) with a and b.", A.Args(
            A.Str("path", "Working tree (default ${APP_ROOT})."), A.Str("a", "First file."), A.Str("b", "Second file."), A.List("paths", "Limit to these paths."))),
        A.Change("pull", "git pull --ff-only; refuses if the tree is dirty.", A.Args(A.Str("path", "Working tree (default ${APP_ROOT}).")), Dirty_)
    };

    public static async Task<string?> Head(ToolContext c, string path)
    {
        var r = await c.Exec("git", new[] { "-C", path, "rev-parse", "HEAD" }, echo: false);
        return r.Code == 0 ? r.Stdout.Trim() : null;
    }

    private static async Task<List<string>> Dirty(ToolContext c, string path)
    {
        var a = new List<string> { "-C", path, "status", "--porcelain" };
        if (!c.Bool("untracked", false)) a.Add("-uno");      // by default only tracked files count
        var paths = c.List("paths");
        if (paths.Count > 0) { a.Add("--"); a.AddRange(paths); }
        var r = await c.Exec("git", a, echo: false);
        if (r.Code != 0) throw new InvalidOperationException($"git status failed in '{path}': {r.Stderr.Trim()}");
        return r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        var path = c.Str("path", "${APP_ROOT}");
        switch (c.Verb)
        {
            case "head":
            {
                var head = await Head(c, path);
                return head is null
                    ? ToolResult.Fail($"'{path}' is not a git working tree")
                    : ToolResult.Success($"{path} @ {head[..Math.Min(7, head.Length)]}", new() { [c.Str("into", "GIT_HEAD")] = head });
            }
            case "status":
            case "require-clean":
            {
                var dirty = await Dirty(c, path);
                foreach (var l in dirty.Take(20)) c.Log(l);
                if (dirty.Count == 0) return ToolResult.Success($"{path} is clean");
                return c.Verb == "status"
                    ? ToolResult.Success($"{dirty.Count} changed file(s)", new() { ["GIT_DIRTY_COUNT"] = dirty.Count.ToString() })
                    : ToolResult.Fail($"{path} has {dirty.Count} uncommitted change(s); commit or stash first (the tool cannot know what you intend)");
            }
            case "diff":
            {
                // a + b: compare any two files (works outside a repo, e.g. for gitignored .env). Otherwise: working-tree diff.
                var a = new List<string>();
                var noIndex = c.Opt("a") is not null;
                if (noIndex) a.AddRange(new[] { "diff", "--no-index", "--no-color", "--", c.Str("a"), c.Str("b") });
                else { a.AddRange(new[] { "-C", path, "diff", "--no-color" }); var p = c.List("paths"); if (p.Count > 0) { a.Add("--"); a.AddRange(p); } }

                var r = await c.Exec("git", a);       // output is echoed to the log
                var okCodes = noIndex ? new[] { 0, 1 } : new[] { 0 };   // --no-index exits 1 when files differ
                if (!okCodes.Contains(r.Code)) return ToolResult.Fail($"git diff failed ({r.Code}): {r.Stderr.Trim()}");
                var changed = r.Stdout.Split('\n').Count(l => (l.StartsWith('+') && !l.StartsWith("+++")) || (l.StartsWith('-') && !l.StartsWith("---")));
                return ToolResult.Success(changed == 0 ? "no differences" : $"{changed} changed line(s)", new() { ["DIFF_LINES"] = changed.ToString() });
            }
            case "pull":
            {
                var dirty = await Dirty(c, path);
                if (dirty.Count > 0) return ToolResult.Fail($"{path} has uncommitted changes; refusing to pull");
                var r = await c.Exec("git", new[] { "-C", path, "pull", "--ff-only" });
                return r.Code == 0 ? ToolResult.Success("pulled (fast-forward only)") : ToolResult.Fail($"git pull failed ({r.Code})");
            }
            default: return ToolResult.Fail($"Unknown verb '{c.Verb}'");
        }
    }
}
