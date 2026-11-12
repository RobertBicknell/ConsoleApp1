using System.Diagnostics;
using System.Text;

namespace ToolRunner.Core;

public sealed record ProcResult(int Code, string Stdout, string Stderr, string Combined);

/// The only place a child process is started. stdout is treated as data only at this boundary,
/// with foreign programs (git, docker, ssh); tools exchange typed values through RunState.
public static class Proc
{
    public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, string? workDir,
                                                  Action<string>? echo, CancellationToken ct, string? stdin = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(false);
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        var gate = new object();
        StringBuilder o = new(), e = new(), all = new();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, d) => { if (d.Data is null) return; lock (gate) { o.AppendLine(d.Data); all.AppendLine(d.Data); } echo?.Invoke(d.Data); };
        p.ErrorDataReceived  += (_, d) => { if (d.Data is null) return; lock (gate) { e.AppendLine(d.Data); all.AppendLine(d.Data); } echo?.Invoke("(stderr) " + d.Data); };

        p.Start();
        using var reg = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { /* already gone */ } });
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        await p.WaitForExitAsync(ct);
        return new ProcResult(p.ExitCode, o.ToString(), e.ToString(), all.ToString());
    }
}
