using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// Checks that git and docker are reachable on a host (local or over ssh). Shared by `doctor` and the UI.
public static class Doctor
{
    private static readonly (string Exe, string[] Args)[] Checks =
    {
        ("git", new[] { "--version" }), ("docker", new[] { "--version" }), ("docker", new[] { "compose", "version" })
    };

    public static async Task<bool> RunAsync(IHost host, Action<string> log, CancellationToken ct)
    {
        var allOk = true;
        foreach (var (exe, a) in Checks)
        {
            try
            {
                var r = await host.ExecAsync(exe, a, null, null, ct);
                log($"{(r.Code == 0 ? "OK  " : "FAIL")} {exe} {string.Join(' ', a)}: {(r.Code == 0 ? r.Stdout.Trim() : r.Stderr.Trim())}");
                allOk &= r.Code == 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log($"FAIL {exe}: {ex.Message}"); allOk = false; }
        }
        return allOk;
    }
}
