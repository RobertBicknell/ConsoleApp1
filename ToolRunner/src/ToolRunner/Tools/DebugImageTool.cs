using System.Text.RegularExpressions;
using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tools;

/// A temporary debuggable image in place of a hardened one. Verbs: candidates, plan, prepare, use, restore.
///   candidates  unhardened base images that could replace a hardened one (registry API and/or local images).
///   plan        works out, without writing anything: the Dockerfile, the build context, which compose service the
///               project is the image of, the base image to switch to, and the tag of the debug image.
///   prepare     plan + writes Dockerfile.debug beside the Dockerfile (and copies vsdbg into the build context if asked).
///   use         points the service at the debug image through a generated compose OVERRIDE file (the original compose
///               file is not touched) and writes the launch.json Visual Studio attaches with.
///   restore     takes the service out of the override file again (the file is deleted when it is empty).
/// The build itself is `docker build`, and starting the service is `compose up` with `optionalFiles`.
public sealed class DebugImageTool : ITool
{
    public string Name => "debugimage";

    private static readonly Arg[] Registry = A.Args(
        A.Str("registryApi", "Registry v2 API base (default ${REGISTRY_API}); Artifactory: https://HOST/artifactory/api/docker/REPO-KEY."),
        A.Str("registryPrefix", "What goes in front of a repository name in an image reference (default ${REGISTRY_PREFIX})."),
        A.Str("registryUser", "User for basic auth (default ${REGISTRY_USER})."),
        A.Str("registryToken", "Password / token; sent as Bearer when there is no user (default ${REGISTRY_TOKEN})."),
        A.Str("repoFilter", "Regex a repository name must match to be read (default: dotnet|aspnet|runtime)."),
        A.Bool("localImages", "Also consider the images `docker image ls` reports (default true)."));

    private static readonly Arg[] Plan = A.Args(
        A.Str("source", "The Dockerfile, or the project folder that contains it.", true),
        A.Str("service", "The compose service whose image this replaces. Default: matched by name."),
        A.Str("baseImage", "The unhardened base image to build on. Default: the best candidate."),
        A.Str("context", "Build context. Default: worked out from the project paths the Dockerfile copies."),
        A.Str("composeFile", "Compose file that defines the service (default ${COMPOSE_FILE})."),
        A.Str("tag", "The debug image, repo:tag. Default: the service's image repo with tag ${VERSION}-debug (without VERSION: the tag the service has now, plus -debug)."),
        A.Bool("asRoot", "Run as root in the debug image (default true)."),
        A.Bool("packages", "Install ps, curl and unzip with the base image's package manager (default true)."),
        A.Str("configuration", "Build configuration that replaces Release (default Debug; empty = leave as is)."),
        A.Enum("vsdbg", "How the debugger gets in. mount (default): bind-mount the vsdbg folder at /remote_debugger when the service starts, as Visual Studio does. copy: bake that folder into the image. download: run GetVsDbg.sh at build time (needs internet).", "mount", "copy", "download"),
        A.Str("vsdbgDir", "Folder that holds vsdbg (default ${VSDBG_DIR}, then %USERPROFILE%/vsdbg/vs2017u5, where Visual Studio keeps it). Its linux-musl-x64 subfolder is used for an alpine base."),
        A.Str("vsdbgUrl", "For vsdbg=download: the GetVsDbg.sh script (default ${VSDBG_URL}, then https://aka.ms/getvsdbgsh)."));

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("candidates", "List unhardened base images that could stand in for a hardened one, best first.", A.Args(
            A.Str("like", "The hardened image to replace (its name gives the kind and the .NET version).", true)), Registry),
        A.Verb("plan", "Work out service, base image, context and tag for a debug image. Writes nothing.", Plan, Registry),
        A.Change("prepare", "plan, then write Dockerfile.debug beside the Dockerfile.", Plan, Registry),
        A.Change("use", "Point a service at its debug image through the generated compose override file; write the Visual Studio launch.json.", A.Args(
            A.Str("service", "Compose service.", true), A.Str("image", "The debug image.", true),
            A.Str("override", "The override file (default ${COMPOSE_DEBUG_IMAGE}, then ${WORK_DIR}/docker-compose.debug-image.yml)."),
            A.Str("composeFile", "Compose file that defines the service (default ${COMPOSE_FILE})."),
            A.Bool("asRoot", "Set `user: root` on the service (default true)."),
            A.Str("vsdbgMount", "Host folder to bind-mount at /remote_debugger (default ${DEBUG_VSDBG_MOUNT}; none = vsdbg is in the image)."),
            A.Str("vsdbgPath", "vsdbg inside the container (default ${DEBUG_VSDBG_PATH}, then /remote_debugger/vsdbg)."),
            A.Str("sourceInContainer", "Where the sources were during the build (default ${DEBUG_SOURCE_ROOT})."),
            A.Str("sourceOnDisk", "The same sources on this machine (default ${DEBUG_CONTEXT})."),
            A.Str("launchJson", "Where to write the launch file (on this machine)."))),
        A.Change("restore", "Take a service out of the compose override file, so the next `up` uses its original image.", A.Args(
            A.Str("service", "Compose service.", true),
            A.Str("override", "The override file (default ${COMPOSE_DEBUG_IMAGE}, then ${WORK_DIR}/docker-compose.debug-image.yml).")))
    };

    public Task<ToolResult> RunAsync(ToolContext c) => c.Verb switch
    {
        "candidates" => Candidates(c),
        "plan" => PlanAsync(c, write: false),
        "prepare" => PlanAsync(c, write: true),
        "use" => Use(c),
        "restore" => Restore(c),
        _ => Task.FromResult(ToolResult.Fail($"Unknown verb '{c.Verb}'"))
    };

    /// An argument, else a state variable; empty counts as absent (so `${service:-}` in a saved command means "not given").
    private static string? Val(ToolContext c, string arg, string? variable = null) =>
        c.Opt(arg) is { Length: > 0 } a ? a : variable is not null && c.Var(variable) is { Length: > 0 } v ? v : null;

    private static string Slash(string p) => p.Replace('\\', '/').TrimEnd('/');
    private static string DirOf(string p) => p.Contains('/') ? p[..p.LastIndexOf('/')] : ".";
    private static string NameOf(string p) => p[(p.LastIndexOf('/') + 1)..];
    private static string OverridePath(ToolContext c) => Slash(Val(c, "override", "COMPOSE_DEBUG_IMAGE") ?? c.State.Expand("${WORK_DIR}/docker-compose.debug-image.yml"));

    private static async Task<(List<ImageCandidate> Ranked, List<string> Notes)> Find(ToolContext c, string like)
    {
        var available = new List<string>();
        var notes = new List<string>();
        if (Val(c, "registryApi", "REGISTRY_API") is { } api)
        {
            var filter = new Regex(Val(c, "repoFilter") ?? "dotnet|aspnet|runtime", RegexOptions.IgnoreCase);
            var (images, n) = await RegistryClient.ListAsync(api, Val(c, "registryPrefix", "REGISTRY_PREFIX"), Val(c, "registryUser", "REGISTRY_USER"),
                Val(c, "registryToken", "REGISTRY_TOKEN"), filter, 25, c.Ct);
            available.AddRange(images); notes.AddRange(n);
            notes.Add($"registry {api}: {images.Count} image tag(s) read");
        }
        else notes.Add("no registry configured (REGISTRY_API); only local images were considered");
        if (c.Bool("localImages", true))
        {
            var r = await c.Exec("docker", ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}"], echo: false);
            if (r.Code == 0) available.AddRange(r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            else notes.Add($"docker image ls failed: {r.Stderr.Trim()}");
        }
        return (DebugImages.Rank(like, available), notes);
    }

    private static async Task<ToolResult> Candidates(ToolContext c)
    {
        var (ranked, notes) = await Find(c, c.Str("like"));
        foreach (var n in notes) c.Log(n);
        foreach (var k in ranked.Take(15)) c.Log($"{k.Image}   ({k.Why})");
        return ranked.Count == 0
            ? ToolResult.Fail($"no unhardened candidate found for {c.Str("like")}")
            : ToolResult.Success($"{ranked.Count} candidate(s); best: {ranked[0].Image}", new() { ["DEBUG_BASE_IMAGE"] = ranked[0].Image, ["DEBUG_BASE_CANDIDATES"] = string.Join("\n", ranked.Select(k => k.Image)) });
    }

    private static async Task<ToolResult> PlanAsync(ToolContext c, bool write)
    {
        // 1. the Dockerfile
        var source = Slash(c.Str("source"));
        var dockerfile = source;
        var text = NameOf(source).Contains("Dockerfile", StringComparison.OrdinalIgnoreCase) ? await c.Host.ReadTextAsync(source, c.Ct) : null;
        if (text is null)
        {
            var found = (await c.Host.FindFilesAsync(source, ["Dockerfile", "Dockerfile.*", "*.Dockerfile"], 0, c.Ct)).Where(f => !f.EndsWith(".debug", StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count != 1) return ToolResult.Fail($"expected exactly one Dockerfile in {source}, found {found.Count}; name the file");
            dockerfile = Slash(found[0]);
            text = await c.Host.ReadTextAsync(dockerfile, c.Ct) ?? "";
        }
        var dir = DirOf(dockerfile);
        var stages = Dockerfiles.Stages(text);
        if (stages.Count == 0) return ToolResult.Fail($"{dockerfile} has no FROM");
        var original = Dockerfiles.RuntimeChain(stages)[^1].Image;

        // 2. the build context: the nearest folder (the Dockerfile's, or up to three above) where the copied project paths exist
        var context = Val(c, "context") is { } given ? Slash(given) : null;
        var projects = Dockerfiles.CopiedProjects(text);
        if (context is null)
        {
            context = dir;
            if (projects.Count > 0)
                for (var (candidate, up) = (dir, 0); up <= 3 && candidate.Contains('/'); candidate = DirOf(candidate), up++)
                    if (await c.Host.ExistsAsync($"{candidate}/{projects[0]}", c.Ct)) { context = candidate; break; }
        }

        // 3. the service this image belongs to
        var composeFile = Slash(Val(c, "composeFile", "COMPOSE_FILE") ?? throw new ArgumentException("needs 'composeFile' (or COMPOSE_FILE)"));
        var composeText = await c.Host.ReadTextAsync(composeFile, c.Ct);
        if (composeText is null) return ToolResult.Fail($"compose file not found: {composeFile}");
        var env = new Dictionary<string, string>();
        if (c.Var("ENV_FILE") is { } envFile && await c.Host.ReadTextAsync(envFile, c.Ct) is { } envText)
            foreach (var v in Scanner.EnvFile(envFile, envText, "env")) env[v.Name] = v.Value ?? "";
        var services = ComposeLayout.Services(composeFile, composeText, env);

        ServiceInfo? service;
        if (Val(c, "service") is { } wanted)
        {
            service = services.FirstOrDefault(s => s.Name == wanted);
            if (service is null) return ToolResult.Fail($"no service '{wanted}' in {composeFile} (it has: {string.Join(", ", services.Select(s => s.Name))})");
        }
        else
        {
            var csproj = (await c.Host.FindFilesAsync(dir, ["*.csproj"], 0, c.Ct)).Select(f => NameOf(Slash(f))[..^".csproj".Length]);
            var names = new[] { Dockerfiles.EntryAssembly(text), NameOf(dir) }.Concat(csproj).Concat(projects.Select(p => NameOf(p)[..^".csproj".Length])).ToList();
            var scored = DebugImages.MatchService(services, names);
            var best = scored.Where(s => s.Score > 0 && s.Score == scored[0].Score).ToList();
            if (best.Count != 1)
                return ToolResult.Fail((best.Count == 0 ? "no compose service matches this project by name" : $"several services match equally ({string.Join(", ", best.Select(b => b.Service.Name))})")
                    + $"; give 'service'. Project names tried: {string.Join(", ", names.Where(n => n is not null).Distinct())}");
            service = best[0].Service;
            c.Log($"service: {service.Name}  (matched by name, score {best[0].Score} of 3; say service=... if that is wrong)");
        }

        // 4. the base image
        var baseImage = Val(c, "baseImage");
        if (baseImage is null)
        {
            var (ranked, notes) = await Find(c, original);
            foreach (var n in notes) c.Log(n);
            foreach (var k in ranked.Take(5)) c.Log($"candidate: {k.Image}   ({k.Why})");
            if (ranked.Count == 0) return ToolResult.Fail($"no unhardened base image found to replace {original}; give 'baseImage'");
            baseImage = ranked[0].Image;
        }

        // 5. names
        var serviceImage = service.Image is null ? null : ImageRef.Parse(service.Image);
        var tag = Val(c, "tag") ?? (serviceImage is null ? $"{service.Name}:debug" : $"{serviceImage.Repo}:{(c.Var("VERSION") is { Length: > 0 } ver ? ver : serviceImage.Tag)}-debug");
        var target = dockerfile.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase) ? dockerfile + ".debug" : $"{dir}/{NameOf(dockerfile).Split('.')[0]}.debug";
        var asRoot = c.Bool("asRoot", true);
        const string inContext = ".toolrunner-vsdbg";
        var mode = Val(c, "vsdbg") ?? "mount";
        var alpine = baseImage.Contains("alpine", StringComparison.OrdinalIgnoreCase);
        string? vsdbgDir = null;
        if (mode != "download")
        {
            // Visual Studio keeps the Linux debugger on the Windows side and mounts it into the containers it debugs
            vsdbgDir = Slash(Val(c, "vsdbgDir", "VSDBG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "vsdbg", "vs2017u5"));
            if (alpine && await c.Host.ExistsAsync($"{vsdbgDir}/linux-musl-x64/vsdbg", c.Ct)) vsdbgDir += "/linux-musl-x64";
            if (!await c.Host.ExistsAsync($"{vsdbgDir}/vsdbg", c.Ct))
                return ToolResult.Fail($"no vsdbg in {vsdbgDir}. Visual Studio puts it there the first time it debugs a Linux container; or fetch it once on a machine with internet: "
                    + $"powershell -Command \"& ([scriptblock]::Create((iwr https://aka.ms/getvsdbgps1).Content)) -Version vs2022 -RuntimeID {(alpine ? "linux-musl-x64" : "linux-x64")} -InstallPath <folder>\" and set VSDBG_DIR to <folder>");
        }
        var copyIn = mode == "copy";

        var (debugText, changes) = Dockerfiles.DebugVariant(text, new DebugDockerfileOptions(baseImage, asRoot, c.Bool("packages", true),
            c.Opt("configuration") ?? "Debug", copyIn ? inContext : null, mode == "download" ? Val(c, "vsdbgUrl", "VSDBG_URL") ?? "https://aka.ms/getvsdbgsh" : null));

        c.Log($"dockerfile:     {dockerfile}");
        c.Log($"build context:  {context}");
        c.Log($"service:        {service.Name}  (image now: {service.Image ?? "(none: built by compose)"})");
        c.Log($"hardened base:  {original}");
        c.Log($"debug base:     {baseImage}");
        c.Log($"debug image:    {tag}");
        c.Log($"app version:    {(serviceImage is null ? "(unknown)" : VersionScheme.From(c.Var).Of(serviceImage.Tag) ?? "(none in the tag)")}  (of the image {service.Name} runs now; the source must be on this version)");
        c.Log($"vsdbg:          {(mode == "mount" ? $"{vsdbgDir} mounted at /remote_debugger" : mode == "copy" ? $"{vsdbgDir} copied to /vsdbg in the image" : "downloaded at build time")}");
        if (alpine && mode != "download" && !vsdbgDir!.EndsWith("linux-musl-x64")) c.Log("note: alpine needs the linux-musl-x64 build of vsdbg; check that this folder holds it");
        foreach (var ch in changes) c.Log($"  {ch}");

        // things that stop a debugger whatever the image is
        var body = DebugImages.ServiceBody(composeText, service.Name);
        if (body.Any(l => Regex.IsMatch(l, @"(DOTNET|COMPlus)_EnableDiagnostics\w*\s*[=:]\s*""?0")) || env.Any(e => e.Key.EndsWith("_EnableDiagnostics") && e.Value == "0"))
            c.Log("note: the deployment sets EnableDiagnostics=0, which disables debugging; the override file sets DOTNET_EnableDiagnostics=1 for this service");
        if (baseImage.Contains("alpine", StringComparison.OrdinalIgnoreCase) != original.Contains("alpine", StringComparison.OrdinalIgnoreCase))
            c.Log("note: the debug base is a different distro family from the original; an app published for linux-musl will not start on glibc (and the reverse)");
        if (Regex.IsMatch(text, @"linux-musl", RegexOptions.IgnoreCase) && !baseImage.Contains("alpine", StringComparison.OrdinalIgnoreCase))
            c.Log("note: the Dockerfile publishes for linux-musl but the debug base does not look like alpine");

        var outputs = new Dictionary<string, string>
        {
            ["DEBUG_SERVICE"] = service.Name, ["DEBUG_IMAGE"] = tag, ["DEBUG_BASE_IMAGE"] = baseImage, ["DEBUG_ORIGINAL_BASE"] = original,
            ["DEBUG_DOCKERFILE"] = target, ["DEBUG_CONTEXT"] = context,
            // the application version this service's image carries: what the local source has to be on for breakpoints to bind
            ["DEBUG_VERSION"] = c.Var("VERSION") is { Length: > 0 } pinned ? pinned : serviceImage is null ? "" : VersionScheme.From(c.Var).Of(serviceImage.Tag) ?? "",
            ["DEBUG_VSDBG_MOUNT"] = mode == "mount" ? vsdbgDir! : "", ["DEBUG_VSDBG_PATH"] = mode == "mount" ? "/remote_debugger/vsdbg" : "/vsdbg/vsdbg"
        };
        if (Dockerfiles.SourceRoot(text) is { } src) outputs["DEBUG_SOURCE_ROOT"] = src;
        if (!write) return ToolResult.Success($"plan: {service.Name} would run {tag}, built on {baseImage}", outputs);

        if (copyIn && vsdbgDir is not null)
        {
            var files = await c.Host.FindFilesAsync(vsdbgDir, ["*"], 6, c.Ct);
            var copied = 0;
            foreach (var f in files.Select(Slash))
            {
                var to = $"{context}/{inContext}/{f[(vsdbgDir.Length + 1)..]}";
                if (await c.Host.HashAsync(f, c.Ct) == await c.Host.HashAsync(to, c.Ct)) continue;
                await c.Host.CopyFileAsync(f, to, c.Ct); copied++;
            }
            c.Log($"vsdbg: {files.Count} file(s) in {context}/{inContext} ({copied} copied)");
        }
        await c.Host.WriteTextAsync(target, debugText, c.Ct);
        c.Log($"Saved to {target}");
        return ToolResult.Success($"wrote {NameOf(target)}: {service.Name} will run {tag}, built on {baseImage}", outputs);
    }

    private static async Task<ToolResult> Use(ToolContext c)
    {
        var (service, image, path) = (c.Str("service"), c.Str("image"), OverridePath(c));
        var composeFile = Val(c, "composeFile", "COMPOSE_FILE");
        var composeText = composeFile is null ? null : await c.Host.ReadTextAsync(composeFile, c.Ct);
        var body = composeText is null ? new List<string>() : DebugImages.ServiceBody(composeText, service);
        if (composeText is not null && body.Count == 0) return ToolResult.Fail($"no service '{service}' in {composeFile}");

        var blocks = DebugImages.ParseOverride(await c.Host.ReadTextAsync(path, c.Ct));
        blocks[service] = DebugImages.OverrideBlock(image, c.Bool("asRoot", true), body.Any(l => Regex.IsMatch(l, @"^\s*read_only:\s*true")), Val(c, "vsdbgMount", "DEBUG_VSDBG_MOUNT"));
        await c.Host.WriteTextAsync(path, DebugImages.RenderOverride(blocks), c.Ct);
        c.Log($"Saved to {path}");
        c.Log($"debug images in use: {string.Join(", ", blocks.Keys)}");

        // the container name compose will give it, for the attach file
        var named = body.Select(l => Regex.Match(l, @"^\s*container_name:\s*(\S+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim('"', '\'');
        var project = c.Var("COMPOSE_PROJECT") ?? NameOf(Slash(c.State.Expand("${APP_ROOT}"))).ToLowerInvariant();
        var container = named ?? $"{project}-{service}-1";
        var ssh = c.Host is SshHost ? c.Var("ssh.target") : null;
        var launch = Val(c, "launchJson") ?? (c.Host is SshHost
            ? Path.Combine(Path.GetTempPath(), "toolrunner", $"attach-{service}.launch.json")
            : c.State.Expand($"${{WORK_DIR}}/attach-{service}.launch.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(launch))!);
        File.WriteAllText(launch, DebugImages.LaunchJson(container, ssh, Val(c, "sourceInContainer", "DEBUG_SOURCE_ROOT"), Val(c, "sourceOnDisk", "DEBUG_CONTEXT"), Val(c, "vsdbgPath", "DEBUG_VSDBG_PATH") ?? "/remote_debugger/vsdbg", engine: c.Var("CONTAINER_ENGINE") is { Length: > 0 } eng ? eng : "docker"));
        c.Log($"Saved to {launch.Replace('\\', '/')}");
        c.Log($"attach from Visual Studio (View > Other Windows > Command Window), once the container is up:");
        c.Log($"  DebugAdapterHost.Launch /LaunchJson:\"{Path.GetFullPath(launch)}\" /EngineGuid:541B8A8A-6081-4506-9F0A-1CE771DEBC04");

        return ToolResult.Success($"{service} -> {image} (override file; the original compose file is unchanged)",
            new() { ["COMPOSE_DEBUG_IMAGE"] = path, ["DEBUG_LAUNCH_JSON"] = Path.GetFullPath(launch), ["DEBUG_CONTAINER"] = container });
    }

    private static async Task<ToolResult> Restore(ToolContext c)
    {
        var (service, path) = (c.Str("service"), OverridePath(c));
        var blocks = DebugImages.ParseOverride(await c.Host.ReadTextAsync(path, c.Ct));
        if (!blocks.Remove(service))
            return ToolResult.Success($"{service} is not using a debug image (nothing to do)", new() { ["COMPOSE_DEBUG_IMAGE"] = path });
        if (blocks.Count == 0) { await c.Host.DeleteAsync(path, c.Ct); c.Log($"deleted {path} (no debug images left in use)"); }
        else { await c.Host.WriteTextAsync(path, DebugImages.RenderOverride(blocks), c.Ct); c.Log($"still on a debug image: {string.Join(", ", blocks.Keys)}"); }
        return ToolResult.Success($"{service} will use its original image at the next `up`", new() { ["COMPOSE_DEBUG_IMAGE"] = path });
    }
}
