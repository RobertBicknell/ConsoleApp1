using ToolRunner.Core;

namespace ToolRunner.Tools;

/// docker compose. Verbs: up, down, stop, start, restart, ps, config.
///
/// Files default to state vars COMPOSE_FILE / ENV_FILE when defined. A derived (generated) compose or env file
/// is used simply by naming it in `files` / `envFile`; the originals stay untouched.
/// --project-directory is always passed (default ${APP_ROOT}) so relative paths and the project name stay
/// the same even when the compose file lives elsewhere, such as .toolrunner/.
public sealed class ComposeTool : ITool
{
    public string Name => "compose";
    private static readonly Arg[] Common = A.Args(
        A.Str("projectDir", "--project-directory (default ${APP_ROOT})."),
        A.Str("project", "Compose project name (-p); default ${COMPOSE_PROJECT}."),
        A.Str("envFile", "--env-file; default ${ENV_FILE}. Name a generated file here to leave the original untouched."),
        A.List("files", "Compose files (-f); default ${COMPOSE_FILE}."),
        A.List("optionalFiles", "Override files added after `files`, each only if it exists (e.g. the debug-image override)."),
        A.Str("service", "One service to act on."),
        A.List("services", "Services to act on."),
        A.List("extra", "Extra arguments passed through to docker compose."));

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Change("up", "Start services detached (-d).", Common, A.Args(
            A.Bool("noDeps", "Pass --no-deps when services are named (default true)."),
            A.Bool("build", "Pass --build."), A.Bool("removeOrphans", "Pass --remove-orphans."),
            A.Str("into", "State variable for the start time (default STARTED_AT_UTC)."))),
        A.Change("down", "Stop and remove the stack.", Common, A.Args(A.Bool("removeOrphans", "Pass --remove-orphans."))),
        A.Change("stop", "Stop services.", Common),
        A.Change("start", "Start existing services.", Common),
        A.Change("restart", "Restart services.", Common),
        A.Verb("ps", "List containers.", Common, A.Args(A.Bool("all", "Include stopped containers (default true)."))),
        A.Verb("config", "Validate and render the merged model.", Common, A.Args(A.Bool("quiet", "Only report errors (-q).")))
    };

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        var verb = c.Verb;
        var a = new List<string> { "compose", "--project-directory", c.Str("projectDir", "${APP_ROOT}") };

        if ((c.Opt("project") ?? c.Var("COMPOSE_PROJECT")) is { } project) { a.Add("-p"); a.Add(project); }
        if ((c.Opt("envFile") ?? c.Var("ENV_FILE")) is { } envFile) { a.Add("--env-file"); a.Add(envFile); }
        var files = c.List("files");
        if (files.Count == 0 && c.Var("COMPOSE_FILE") is { } cf) files.Add(cf);
        foreach (var f in c.List("optionalFiles")) if (f.Length > 0 && await c.Host.ExistsAsync(f, c.Ct)) files.Add(f);
        foreach (var f in files) { a.Add("-f"); a.Add(f); }

        var services = c.List("services");
        if (c.Opt("service") is { } one) services.Insert(0, one);
        var extra = c.List("extra");

        var outputs = new Dictionary<string, string>();
        switch (verb)
        {
            case "up":
                a.AddRange(new[] { "up", "-d" });
                // Without --no-deps, `up <svc>` also starts everything <svc> depends_on, which would silently
                // bring back a service you just stopped for debugging.
                if (services.Count > 0 && c.Bool("noDeps", true)) a.Add("--no-deps");
                if (c.Bool("build", false)) a.Add("--build");
                if (c.Bool("removeOrphans", false)) a.Add("--remove-orphans");
                a.AddRange(extra); a.AddRange(services);
                // Taken from the HOST's clock before starting, so later log queries use the right reference.
                outputs[c.Str("into", "STARTED_AT_UTC")] = Time.Rfc3339(await c.Host.NowUtcAsync(c.Ct));
                break;
            case "down":
                a.Add("down"); if (c.Bool("removeOrphans", false)) a.Add("--remove-orphans"); a.AddRange(extra);
                break;
            case "stop": case "start": case "restart":
                a.Add(verb); a.AddRange(extra); a.AddRange(services);
                if (verb != "stop") outputs["STARTED_AT_UTC"] = Time.Rfc3339(await c.Host.NowUtcAsync(c.Ct));
                break;
            case "ps":
                a.Add("ps"); if (c.Bool("all", true)) a.Add("-a"); a.AddRange(extra); a.AddRange(services);
                break;
            case "config":   // validates and renders the merged model; `quiet` only reports errors
                a.Add("config"); if (c.Bool("quiet", false)) a.Add("-q"); a.AddRange(extra);
                break;
            default: return ToolResult.Fail($"Unknown verb '{verb}'");
        }

        var r = await c.Exec("docker", a);
        var what = $"docker compose {verb}" + (services.Count > 0 ? " " + string.Join(' ', services) : "");
        return r.Code == 0 ? ToolResult.Success($"{what} exited 0", outputs) : ToolResult.Fail($"{what} exited {r.Code}");
    }
}
