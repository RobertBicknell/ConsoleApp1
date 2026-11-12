using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using ToolRunner.Core;

namespace ToolRunner.Hosts;

/// Where commands run and files live. Tools only ever talk to IHost, so the same workflow
/// runs locally or on a Linux server over ssh (--host NAME).
public interface IHost
{
    string Name { get; }
    Task<ProcResult> ExecAsync(string exe, IReadOnlyList<string> args, string? workDir, Action<string>? echo, CancellationToken ct);
    Task<string?> ReadTextAsync(string path, CancellationToken ct);           // null if the file does not exist
    Task WriteTextAsync(string path, string content, CancellationToken ct);   // creates parent directories
    Task<bool> DeleteAsync(string path, CancellationToken ct);                // true if it existed
    Task<DateTimeOffset> NowUtcAsync(CancellationToken ct);                   // the HOST's clock, not ours
    Task<bool> ExistsAsync(string path, CancellationToken ct);                // a file or a directory
    Task<string?> HashAsync(string path, CancellationToken ct);               // SHA-256 (hex) of a file's bytes; null if it does not exist
    Task CopyFileAsync(string source, string target, CancellationToken ct);   // byte-for-byte, both on this host; creates parent directories
    /// Files under `dir` whose NAME matches any simple pattern (* and ?), up to `maxDepth` levels of subdirectory,
    /// skipping directories that are never config (.git, node_modules, bin, obj, ...). Paths use '/'.
    Task<IReadOnlyList<string>> FindFilesAsync(string dir, IReadOnlyList<string> namePatterns, int maxDepth, CancellationToken ct);
    string Combine(string a, string b);
}

/// Directories a recursive search never enters.
public static class SkipDirs
{
    public static readonly string[] Names = { ".git", "node_modules", "bin", "obj", ".toolrunner", ".venv", "__pycache__", ".idea", ".vs" };
}

public sealed class LocalHost : IHost
{
    public string Name => "local";

    /// The container CLI that stands in wherever a tool runs "docker": docker (default), podman, or a full path.
    public string Engine { get; init; } = "docker";

    public Task<ProcResult> ExecAsync(string exe, IReadOnlyList<string> args, string? workDir, Action<string>? echo, CancellationToken ct) =>
        Proc.RunAsync(exe == "docker" ? Engine : exe, args, workDir, echo, ct);

    public async Task<string?> ReadTextAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

    public async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
    }

    public Task<bool> DeleteAsync(string path, CancellationToken ct)
    {
        var existed = File.Exists(path);
        if (existed) File.Delete(path);
        return Task.FromResult(existed);
    }

    public Task<DateTimeOffset> NowUtcAsync(CancellationToken ct) => Task.FromResult(DateTimeOffset.UtcNow);
    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(path) || Directory.Exists(path));

    public Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.Copy(source, target, overwrite: true);
        return Task.CompletedTask;
    }

    public async Task<string?> HashAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(s, ct)).ToLowerInvariant();
    }

    public Task<IReadOnlyList<string>> FindFilesAsync(string dir, IReadOnlyList<string> namePatterns, int maxDepth, CancellationToken ct)
    {
        var found = new List<string>();
        void Walk(string d, int depth)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                    if (namePatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(f), ignoreCase: true))) found.Add(f.Replace('\\', '/'));
                if (depth >= maxDepth) return;
                foreach (var s in Directory.EnumerateDirectories(d))
                    if (!SkipDirs.Names.Contains(Path.GetFileName(s), StringComparer.OrdinalIgnoreCase)) Walk(s, depth + 1);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { /* unreadable, missing, or a file rather than a directory: skip it */ }
        }
        Walk(dir, 0);
        return Task.FromResult<IReadOnlyList<string>>(found.Order(StringComparer.Ordinal).ToList());
    }
    public string Combine(string a, string b) => Path.Combine(a, b);
}

/// Runs everything through the system `ssh` client (OpenSSH ships with Windows 10/11 as an optional feature).
/// Key-based auth only (BatchMode), so a missing key fails fast instead of hanging on a prompt.
/// Paths are POSIX paths on the remote machine and should be absolute.
public sealed class SshHost(string target, int? port, string? identity, IEnumerable<string>? options) : IHost
{
    public string Name => $"ssh:{target}";
    /// The container CLI that stands in wherever a tool runs "docker": docker (default), podman, or a full path.
    public string Engine { get; init; } = "docker";

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private IEnumerable<string> SshArgs()
    {
        yield return "-o"; yield return "BatchMode=yes";
        yield return "-o"; yield return "ConnectTimeout=10";
        foreach (var o in options ?? []) { yield return "-o"; yield return o; }
        if (port is not null) { yield return "-p"; yield return port.Value.ToString(CultureInfo.InvariantCulture); }
        if (identity is not null) { yield return "-i"; yield return identity; }
        yield return target;
    }

    private Task<ProcResult> Remote(string command, Action<string>? echo, CancellationToken ct, string? stdin = null) =>
        Proc.RunAsync("ssh", SshArgs().Append(command), null, echo, ct, stdin);

    public Task<ProcResult> ExecAsync(string exe, IReadOnlyList<string> args, string? workDir, Action<string>? echo, CancellationToken ct)
    {
        var cmd = (workDir is null ? "" : $"cd {Q(workDir)} && ") + "exec " + string.Join(' ', args.Prepend(exe == "docker" ? Engine : exe).Select(Q));
        return Remote(cmd, echo, ct);
    }

    public async Task<string?> ReadTextAsync(string path, CancellationToken ct)
    {
        var r = await Remote($"if [ -f {Q(path)} ]; then cat {Q(path)}; else exit 3; fi", null, ct);
        if (r.Code == 3) return null;
        if (r.Code != 0) throw new IOException($"ssh read of {path} failed ({r.Code}): {r.Stderr.Trim()}");
        return r.Stdout;
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        var r = await Remote($"mkdir -p -- \"$(dirname -- {Q(path)})\" && cat > {Q(path)}", null, ct, content);
        if (r.Code != 0) throw new IOException($"ssh write of {path} failed ({r.Code}): {r.Stderr.Trim()}");
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct)
    {
        var r = await Remote($"if [ -e {Q(path)} ]; then rm -f -- {Q(path)}; else exit 3; fi", null, ct);
        if (r.Code == 3) return false;
        if (r.Code != 0) throw new IOException($"ssh delete of {path} failed ({r.Code}): {r.Stderr.Trim()}");
        return true;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var r = await Remote($"test -e {Q(path)}", null, ct);
        if (r.Code > 1) throw new IOException($"ssh test of {path} failed ({r.Code}): {r.Stderr.Trim()}");
        return r.Code == 0;
    }

    public async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        var r = await Remote($"mkdir -p -- \"$(dirname -- {Q(target)})\" && cp -f -- {Q(source)} {Q(target)}", null, ct);
        if (r.Code != 0) throw new IOException($"ssh copy to {target} failed ({r.Code}): {r.Stderr.Trim()}");
    }

    public async Task<string?> HashAsync(string path, CancellationToken ct)
    {
        var r = await Remote($"if [ -f {Q(path)} ]; then sha256sum -- {Q(path)}; else exit 3; fi", null, ct);
        if (r.Code == 3) return null;
        if (r.Code != 0) throw new IOException($"ssh hash of {path} failed ({r.Code}): {r.Stderr.Trim()}");
        return r.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim().ToLowerInvariant();
    }

    public async Task<IReadOnlyList<string>> FindFilesAsync(string dir, IReadOnlyList<string> namePatterns, int maxDepth, CancellationToken ct)
    {
        // maxdepth counts the start directory as 0, so files in `dir` itself are depth 1
        var skip = string.Join(" -o ", SkipDirs.Names.Select(d => $"-name {Q(d)}"));
        var names = string.Join(" -o ", namePatterns.Select(p => $"-name {Q(p)}"));
        var r = await Remote($"find {Q(dir)} -maxdepth {maxDepth + 1} \\( {skip} \\) -prune -o -type f \\( {names} \\) -print", null, ct);
        if (r.Code > 1) throw new IOException($"ssh find in {dir} failed ({r.Code}): {r.Stderr.Trim()}");      // 1 = some subdirectory unreadable
        return r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Order(StringComparer.Ordinal).ToList();
    }

    // Second precision: portable (no GNU-only %N), and rounding down only widens a --since window slightly.
    public async Task<DateTimeOffset> NowUtcAsync(CancellationToken ct)
    {
        var r = await Remote("date -u +%Y-%m-%dT%H:%M:%SZ", null, ct);
        if (r.Code != 0) throw new IOException($"ssh 'date' failed ({r.Code}): {r.Stderr.Trim()}");
        return DateTimeOffset.Parse(r.Stdout.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    public string Combine(string a, string b) => a.TrimEnd('/') + "/" + b.TrimStart('/');
}
