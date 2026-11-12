using System.Diagnostics;
using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;
using ToolRunner.Tools;

namespace ToolRunner.Tests;

/// Where the repo is, found by walking up from the test binaries to ToolRunner.sln.
public static class Repo
{
    public static string Root { get; } = Find();
    public static string ShippedConfig => Path.Combine(Root, "src", "ToolRunner", "config");
    public static string SourceDir(string sub) => Path.Combine(Root, "src", "ToolRunner", sub);

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ToolRunner.sln"))) return d.FullName;
        throw new InvalidOperationException("ToolRunner.sln not found above " + AppContext.BaseDirectory);
    }

    /// The fake docker, newest build of Debug/Release.
    public static string FakeDockerDir() =>
        new[] { "Debug", "Release" }.Select(c => Path.Combine(Root, "src", "FakeDocker", "bin", c, "net9.0"))
            .Where(d => File.Exists(Path.Combine(d, "docker.exe")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "docker.exe"))).FirstOrDefault()
        ?? throw new InvalidOperationException("FakeDocker is not built");
}

/// Serialises everything that touches PATH / FAKEDOCKER_STATE (process-wide), so these tests never overlap.
[CollectionDefinition("sandbox", DisableParallelization = true)]
public sealed class SandboxCollection;

public sealed record Outcome(bool Ok, IReadOnlyList<string> Log, RunState State)
{
    public string Text => string.Join("\n", Log);
}

/// A disposable world in a temp directory: two git repos (myapp, pluginhost), a copy of the shipped config pointing at
/// them, and the fake docker first on PATH. Tests drive the real engine in-process against it.
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "toolrunner-test-" + Guid.NewGuid().ToString("N")[..8]);
    public string App => Path.Combine(Root, "myapp");
    public string Plugin => Path.Combine(Root, "pluginhost");
    public string ConfigDir => Path.Combine(Root, "config");
    public string Env => Path.Combine(App, ".env");
    public string DebugEnv => Path.Combine(App, ".toolrunner", "debug.env");

    private readonly string? _oldPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string? _oldState = Environment.GetEnvironmentVariable("FAKEDOCKER_STATE");

    public Sandbox()
    {
        Directory.CreateDirectory(App); Directory.CreateDirectory(Plugin); Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path.Combine(App, "docker-compose.yml"),
            "services:\n  pluginhost:\n    image: myorg/pluginhost:dev\n  gateway:\n    image: myorg/gateway:dev\n    ports:\n      - \"${GATEWAY_PORT:-8080}:80\"\n    environment:\n      - LOG_LEVEL=${LOG_LEVEL}\n    depends_on:\n      - pluginhost\n");
        File.WriteAllText(Env, "PLUGINHOST_HOST=pluginhost\nPLUGINHOST_PORT=5000\nLOG_LEVEL=info\n");
        File.WriteAllText(Path.Combine(App, ".gitignore"), ".env\n.toolrunner/\n");
        File.WriteAllText(Path.Combine(Plugin, "main.py"), "print('plugin host')\n");
        foreach (var r in new[] { App, Plugin }) { Git(r, "init", "-q"); Commit(r, "init"); }

        foreach (var f in new[] { "globals.json", "args.json", "workflows.json" })
            File.Copy(Path.Combine(Repo.ShippedConfig, f), Path.Combine(ConfigDir, f));
        File.WriteAllText(Path.Combine(ConfigDir, "globals.user.json"), $$"""{ "APP_ROOT": "{{App.Replace('\\', '/')}}" }""");

        Environment.SetEnvironmentVariable("PATH", Repo.FakeDockerDir() + Path.PathSeparator + _oldPath);
        Environment.SetEnvironmentVariable("FAKEDOCKER_STATE", Path.Combine(Root, "fakedocker-state.json"));
    }

    public string DockerCalls => File.Exists(Path.Combine(Root, "fakedocker-state.json.calls.log"))
        ? File.ReadAllText(Path.Combine(Root, "fakedocker-state.json.calls.log")) : "";

    public static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", dir, "-c", "user.name=test", "-c", "user.email=test@local" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}");
    }

    public static void Commit(string dir, string message) { Git(dir, "add", "-A"); Git(dir, "commit", "-q", "-m", message); }

    public RunState NewState() => ConfigLoader.LoadState(ConfigDir, null, []);

    /// Runs a workflow from workflows.json, binding positional parameters like the command line does.
    public async Task<Outcome> Run(string workflow, params string[] positional)
    {
        var cfg = ConfigLoader.LoadConfig(ConfigDir);
        var wf = cfg.Workflows[workflow];
        var state = NewState();
        foreach (var (p, v) in wf.Params.Zip(positional)) state.Vars[p] = v;
        return await Execute(cfg, wf.Steps, state);
    }

    /// Runs one step (tool plus inline arguments), for set-up such as stamping an image.
    public async Task<Outcome> RunStep(string tool, JsonObject with) =>
        await Execute(ConfigLoader.LoadConfig(ConfigDir), [new StepDef { Run = tool, With = with }], NewState());

    /// Builds the image the way a developer would, stamping it with the pluginhost commit so source/verify passes.
    public Task<Outcome> Stamp() => RunStep("docker", new JsonObject
    {
        ["verb"] = "build", ["tag"] = "myorg/pluginhost:dev", ["context"] = ".", ["revisionFrom"] = Plugin.Replace('\\', '/')
    });

    private static async Task<Outcome> Execute(ConfigSet cfg, IReadOnlyList<StepDef> steps, RunState state)
    {
        var log = new List<string>();
        var ok = await new WorkflowRunner(Registry.Default(), cfg, log.Add).RunAsync(steps, state, new LocalHost(), false, CancellationToken.None);
        return new Outcome(ok, log, state);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _oldPath);
        Environment.SetEnvironmentVariable("FAKEDOCKER_STATE", _oldState);
        try { ClearReadOnly(Root); Directory.Delete(Root, true); } catch (IOException) { /* a leftover temp dir is harmless */ }
    }

    private static void ClearReadOnly(string dir)    // git object files are read-only on Windows
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
    }
}
