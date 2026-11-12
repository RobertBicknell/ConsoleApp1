using System.IO.Compression;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// What git says about a folder. Changed is `git status --porcelain` (two status letters, then the path relative to Root).
public sealed record GitFacts(string Root, string Branch, string Head, string? Subject, IReadOnlyList<string> Changed);

/// Almost every folder this tool is pointed at is a git working tree. That gives two things for free:
/// what has changed since the last commit, and any commit as a known-good reference to compare with.
public static class GitRepo
{
    private static async Task<string?> Git(IHost host, string dir, CancellationToken ct, params string[] args)
    {
        try
        {
            var r = await host.ExecAsync("git", ["-C", dir, .. args], null, null, ct);
            return r.Code == 0 ? r.Stdout.TrimEnd('\r', '\n') : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return null; }     // no git here
    }

    /// Null if the folder is not inside a git working tree (or git is not available on the host).
    public static async Task<GitFacts?> FactsAsync(IHost host, string dir, CancellationToken ct)
    {
        if (await Git(host, dir, ct, "rev-parse", "--show-toplevel") is not { Length: > 0 } root) return null;
        var branch = await Git(host, dir, ct, "rev-parse", "--abbrev-ref", "HEAD") ?? "?";
        var head = await Git(host, dir, ct, "rev-parse", "--short", "HEAD") ?? "(no commits)";
        var subject = await Git(host, dir, ct, "log", "-1", "--format=%s");
        var status = await Git(host, dir, ct, "status", "--porcelain") ?? "";
        return new GitFacts(root.Replace('\\', '/'), branch, head, subject, status.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3).ToList());
    }

    /// "git:REF" as a reference to compare with: writes the folder as it was at REF (a branch, tag or commit) into
    /// `cacheDir` and returns that path. Uses `git archive`, so only committed content appears. Local folders only.
    public static async Task<string> MaterialiseAsync(IHost host, string dir, string gitRef, string cacheDir, CancellationToken ct)
    {
        if (host is not LocalHost) throw new InvalidOperationException("comparing with a git ref (git:REF) works on local folders only for now; over ssh, check the ref out into a folder and compare with that");
        var commit = await Git(host, dir, ct, "rev-parse", "--verify", "--quiet", gitRef + "^{commit}")
                     ?? throw new ArgumentException($"'{gitRef}' is not a commit in the repository containing {dir}");
        var prefix = (await Git(host, dir, ct, "rev-parse", "--show-prefix") ?? "").Trim().TrimEnd('/');

        var target = Path.Combine(cacheDir, $"{commit[..12]}-{(prefix.Length == 0 ? "root" : prefix.Replace('/', '_'))}");
        if (Directory.Exists(target)) return target.Replace('\\', '/');      // a commit never changes, so the copy is reusable

        Directory.CreateDirectory(cacheDir);
        var zip = target + ".zip";
        // run from the top of the repository: from a subfolder, git archive would apply that folder's prefix a second time
        var top = await Git(host, dir, ct, "rev-parse", "--show-toplevel") ?? dir;
        var r = await host.ExecAsync("git", ["-C", top, "-c", "core.autocrlf=false", "archive", "--format=zip", "-o", zip, prefix.Length == 0 ? commit : $"{commit}:{prefix}"], null, null, ct);
        if (r.Code != 0) throw new IOException($"git archive failed: {r.Stderr.Trim()}");
        ZipFile.ExtractToDirectory(zip, target);
        File.Delete(zip);
        return target.Replace('\\', '/');
    }
}
