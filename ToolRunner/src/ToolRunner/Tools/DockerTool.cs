using ToolRunner.Core;

namespace ToolRunner.Tools;

/// docker CLI. Verbs: ps, inspect, logs, history, events, build, run.
///
/// A container is named with `target` (name or id) or with `service` (+ optional `project`), resolved through
/// the com.docker.compose.* labels, so you never need the generated container name.
public sealed class DockerTool : ITool
{
    public string Name => "docker";
    private static readonly Arg[] Target = A.Args(
        A.Str("target", "Container name or id."),
        A.Str("service", "Compose service; resolved through the com.docker.compose.* labels."),
        A.Str("project", "Compose project to narrow `service` (default ${COMPOSE_PROJECT})."));

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("ps", "docker ps.", A.Args(A.Bool("all", "Include stopped containers (-a)."), A.List("filter", "--filter expressions."),
            A.Str("format", "--format template."), A.Str("into", "State variable for the output (default DOCKER_PS)."))),
        A.Verb("inspect", "docker inspect a container.", Target, A.Args(A.Str("format", "--format template."),
            A.Str("into", "State variable (default DOCKER_INSPECT)."))),
        A.Verb("history", "docker history of an image.", A.Args(A.Str("image", "Image name.", true), A.Str("format", "--format template."),
            A.Str("into", "State variable (default DOCKER_HISTORY)."))),
        A.Verb("logs", "Container logs, optionally waiting for text.", Target, LogMatcher.Spec, A.Args(
            A.Str("since", "Default: STARTED_AT_UTC from the last compose up."), A.Str("until", "Upper bound."),
            A.Int("tail", "Number of lines from the end."), A.Bool("timestamps", "Show timestamps (default true)."),
            A.Int("waitSeconds", "Poll up to this long for the expectation to hold."))),
        A.Verb("events", "docker events between since and now.", A.Args(
            A.Str("since", "Default: STARTED_AT_UTC."), A.Str("until", "Default: now on the host."),
            A.List("filter", "--filter expressions."), A.Str("format", "--format template (default {{json .}}).")), LogMatcher.Spec),
        A.Change("build", "docker build.", A.Args(
            A.Str("context", "Build context.", true), A.List("tag", "Image tags (-t)."), A.Str("file", "Dockerfile (-f)."),
            A.Bool("noCache", "--no-cache."), A.Obj("buildArgs", "--build-arg KEY=VALUE pairs."), A.Obj("labels", "--label KEY=VALUE pairs."),
            A.Str("revisionFrom", "Git working tree whose HEAD is stamped as org.opencontainers.image.revision."))),
        A.Change("run", "docker run.", A.Args(
            A.Str("image", "Image.", true), A.List("cmd", "Command and arguments."), A.Bool("rm", "--rm (default true)."),
            A.Bool("detach", "-d."), A.Str("name", "--name."), A.Str("network", "--network."), A.Obj("env", "-e KEY=VALUE pairs."),
            A.List("ports", "-p mappings."), A.List("volumes", "-v mappings."), A.List("extra", "Extra docker run arguments."),
            A.Str("into", "State variable (default DOCKER_RUN).")))
    };

    internal static async Task<(string? Id, string? Error)> Resolve(ToolContext c)
    {
        if (c.Opt("target") is { } t) return (t, null);
        if (c.Opt("service") is not { } svc) return (null, "needs 'target' or 'service'");

        var a = new List<string> { "ps", "-a", "-q", "--filter", $"label=com.docker.compose.service={svc}" };
        if ((c.Opt("project") ?? c.Var("COMPOSE_PROJECT")) is { } p) { a.Add("--filter"); a.Add($"label=com.docker.compose.project={p}"); }
        var r = await c.Exec("docker", a, echo: false);
        if (r.Code != 0) return (null, $"docker ps failed: {r.Stderr.Trim()}");
        var ids = r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return ids.Count switch
        {
            0 => (null, $"no container found for service '{svc}'"),
            1 => (ids[0], null),
            _ => (null, $"{ids.Count} containers for service '{svc}' (scaled?); use 'target'")
        };
    }

    public async Task<ToolResult> RunAsync(ToolContext c)
    {
        switch (c.Verb)
        {
            case "ps":
            {
                var a = new List<string> { "ps" };
                if (c.Bool("all", false)) a.Add("-a");
                foreach (var f in c.List("filter")) { a.Add("--filter"); a.Add(f); }
                if (c.Opt("format") is { } fmt) { a.Add("--format"); a.Add(fmt); }
                var r = await c.Exec("docker", a);
                return r.Code == 0
                    ? ToolResult.Success("docker ps", new() { [c.Str("into", "DOCKER_PS")] = r.Stdout.TrimEnd() })
                    : ToolResult.Fail($"docker ps exited {r.Code}");
            }
            case "inspect":
            {
                var (id, err) = await Resolve(c); if (id is null) return ToolResult.Fail(err!);
                var a = new List<string> { "inspect" };
                if (c.Opt("format") is { } fmt) { a.Add("--format"); a.Add(fmt); }
                a.Add(id);
                var r = await c.Exec("docker", a, echo: false);
                return r.Code == 0
                    ? ToolResult.Success($"inspected {id}", new() { [c.Str("into", "DOCKER_INSPECT")] = r.Stdout.Trim() })
                    : ToolResult.Fail($"docker inspect exited {r.Code}: {r.Stderr.Trim()}");
            }
            case "history":
            {
                var a = new List<string> { "history", "--no-trunc" };
                if (c.Opt("format") is { } fmt) { a.Add("--format"); a.Add(fmt); }
                a.Add(c.Str("image"));
                var r = await c.Exec("docker", a, echo: false);
                return r.Code == 0
                    ? ToolResult.Success($"history of {c.Str("image")}", new() { [c.Str("into", "DOCKER_HISTORY")] = r.Stdout.TrimEnd() })
                    : ToolResult.Fail($"docker history exited {r.Code}: {r.Stderr.Trim()}");
            }
            case "logs":
            {
                var (id, err) = await Resolve(c); if (id is null) return ToolResult.Fail(err!);
                var a = new List<string> { "logs" };
                // since defaults to the STARTED_AT_UTC that `compose up` recorded: "logs since I started it".
                if ((c.Opt("since") ?? c.Var("STARTED_AT_UTC")) is { } since) { a.Add("--since"); a.Add(since); }
                if (c.Opt("until") is { } until) { a.Add("--until"); a.Add(until); }
                if (c.Opt("tail") is { } tail) { a.Add("--tail"); a.Add(tail); }
                if (c.Bool("timestamps", true)) a.Add("-t");
                a.Add(id);

                var m = new LogMatcher(c);
                string? failure = null;
                var (text, lines, matches, ok) = await m.PollAsync(async () =>
                {
                    var r = await c.Exec("docker", a, echo: false);
                    if (r.Code != 0) failure = $"docker logs exited {r.Code}: {r.Stderr.Trim()}";
                    return r.Combined;     // stdout + stderr of the container, in arrival order
                }, c.Int("waitSeconds", 0), c.Ct);
                if (failure is not null) return ToolResult.Fail(failure);

                m.Show(c, text, matches);
                var outs = LogMatcher.Outputs(c, "LOG", text, lines, matches);
                var summary = $"{lines} line(s), {matches.Count} match(es)";
                return ok ? ToolResult.Success(summary, outs)
                          : ToolResult.Fail(m.Absent ? $"unwanted text present: {summary}" : $"expected text not found: {summary}");
            }
            case "events":
            {
                var since = c.Opt("since") ?? c.Var("STARTED_AT_UTC") ?? throw new ArgumentException("events needs 'since' (or a prior compose up)");
                var until = c.Opt("until") ?? Time.Rfc3339(await c.Host.NowUtcAsync(c.Ct));   // --until is what makes `docker events` terminate
                var a = new List<string> { "events", "--since", since, "--until", until };
                foreach (var f in c.List("filter")) { a.Add("--filter"); a.Add(f); }
                a.Add("--format"); a.Add(c.Str("format", "{{json .}}"));

                var r = await c.Exec("docker", a, echo: false);
                if (r.Code != 0) return ToolResult.Fail($"docker events exited {r.Code}: {r.Stderr.Trim()}");
                var m = new LogMatcher(c);
                var (lines, matches) = m.Evaluate(r.Stdout);
                m.Show(c, r.Stdout, matches);
                var outs = LogMatcher.Outputs(c, "EVENTS", r.Stdout, lines, matches);
                return m.Satisfied(matches.Count)
                    ? ToolResult.Success($"{lines} event(s), {matches.Count} match(es)", outs)
                    : ToolResult.Fail($"event expectation not met ({lines} events, {matches.Count} matches)");
            }
            case "build":
            {
                var a = new List<string> { "build" };
                foreach (var t in c.List("tag")) { a.Add("-t"); a.Add(t); }
                if (c.Opt("file") is { } f) { a.Add("-f"); a.Add(f); }
                if (c.Bool("noCache", false)) a.Add("--no-cache");
                if (c.Args["buildArgs"] is System.Text.Json.Nodes.JsonObject ba) foreach (var (k, v) in ba) { a.Add("--build-arg"); a.Add($"{k}={ToolContext.NodeStr(v)}"); }
                if (c.Args["labels"] is System.Text.Json.Nodes.JsonObject lb) foreach (var (k, v) in lb) { a.Add("--label"); a.Add($"{k}={ToolContext.NodeStr(v)}"); }
                if (c.Opt("revisionFrom") is { } repo)       // stamps the image so `source/verify` (label strategy) can check it later
                {
                    var head = await GitTool.Head(c, repo) ?? throw new InvalidOperationException($"'{repo}' is not a git working tree");
                    a.Add("--label"); a.Add($"org.opencontainers.image.revision={head}");
                }
                a.Add(c.Str("context"));
                var r = await c.Exec("docker", a);
                var first = c.List("tag").FirstOrDefault();
                return r.Code == 0
                    ? ToolResult.Success("built " + (first ?? "image"), first is null ? null : new() { ["IMAGE"] = first })
                    : ToolResult.Fail($"docker build exited {r.Code}");
            }
            case "run":
            {
                var a = new List<string> { "run" };
                if (c.Bool("rm", true)) a.Add("--rm");
                if (c.Bool("detach", false)) a.Add("-d");
                if (c.Opt("name") is { } name) { a.Add("--name"); a.Add(name); }
                if (c.Opt("network") is { } net) { a.Add("--network"); a.Add(net); }
                if (c.Args["env"] is System.Text.Json.Nodes.JsonObject env) foreach (var (k, v) in env) { a.Add("-e"); a.Add($"{k}={ToolContext.NodeStr(v)}"); }
                foreach (var p in c.List("ports")) { a.Add("-p"); a.Add(p); }
                foreach (var v in c.List("volumes")) { a.Add("-v"); a.Add(v); }
                a.AddRange(c.List("extra"));
                a.Add(c.Str("image"));
                a.AddRange(c.List("cmd"));
                var r = await c.Exec("docker", a);
                return r.Code == 0
                    ? ToolResult.Success("docker run exited 0", new() { [c.Str("into", "DOCKER_RUN")] = r.Stdout.TrimEnd() })
                    : ToolResult.Fail($"docker run exited {r.Code}");
            }
            default: return ToolResult.Fail($"Unknown verb '{c.Verb}'");
        }
    }
}
