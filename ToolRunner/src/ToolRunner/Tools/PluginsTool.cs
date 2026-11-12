using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ToolRunner.Core;

namespace ToolRunner.Tools;

/// Plugin binaries vs configuration. Verbs: verify, publish, loaded.
///   verify   compares the assembly names an appsettings file lists with the .dll files under host directories.
///   publish  builds a project, copies its output into a service's plugin folder (unless that folder IS the build
///            output), and makes sure the appsettings file lists the plugin assemblies. The appsettings file is
///            backed up before it is rewritten.
///   loaded   reads a service's logs and reports, per assembly, whether it is mentioned and whether any of those
///            lines is an error.
public sealed class PluginsTool : ITool
{
    public string Name => "plugins";

    public IReadOnlyList<VerbSpec> Spec { get; } = new[]
    {
        A.Verb("verify", "Check that every assembly an appsettings file lists exists under the given directories.", A.Args(
            A.Str("appsettings", "The appsettings file the container loads.", true),
            A.List("dirs", "Host directories to search for the .dll files (the bind-mount sources).", true),
            A.Str("key", "Config path whose strings are the assembly names, e.g. Plugins:Assemblies. Omit to detect them."),
            A.Str("into", "State variable for the number of missing assemblies (default PLUGINS_MISSING)."))),
        A.Change("publish", "Build a plugin project, copy its output to the service's plugin folder and list its assemblies in appsettings.", A.Args(
            A.Str("project", "The .csproj, or the folder that contains it.", true),
            A.Str("dir", "The plugin folder on the host (the source of the service's plugin bind mount).", true),
            A.Str("appsettings", "The appsettings file the service loads.", true),
            A.Str("key", "Config path of the list of plugin assemblies (default Plugins:Assemblies)."),
            A.List("assemblies", "The plugin assemblies to list (not their dependencies). Default: the project's own assembly."),
            A.Str("configuration", "Build configuration (default Debug)."),
            A.Bool("build", "Run `dotnet build` first (default true)."),
            A.Str("output", "The build output folder, if it cannot be found under the project's bin folder."))),
        A.Verb("loaded", "Check a service's logs for evidence that the plugin assemblies loaded.", A.Args(
            A.Str("service", "Compose service whose logs to read."),
            A.Str("target", "Container name or id (instead of service)."),
            A.Str("project", "Compose project to narrow `service`."),
            A.List("assemblies", "Assembly names to look for. Default: the ones the appsettings file lists."),
            A.Str("appsettings", "Appsettings file to take the names from."),
            A.Str("key", "Config path of the list of plugin assemblies."),
            A.Str("since", "Only logs since this time (default: STARTED_AT_UTC from the last restart or up)."),
            A.Bool("allowSilent", "Do not fail when an assembly is never mentioned in the logs (default false).")))
    };

    public Task<ToolResult> RunAsync(ToolContext c) => c.Verb switch
    {
        "verify" => Verify(c),
        "publish" => Publish(c),
        "loaded" => Loaded(c),
        _ => Task.FromResult(ToolResult.Fail($"Unknown verb '{c.Verb}'"))
    };

    private static async Task<ToolResult> Verify(ToolContext c)
    {
        var report = await PluginCheck.CheckAsync(c.Host, c.Str("appsettings"), null, c.List("dirs"), c.Opt("key"), c.Ct);
        foreach (var i in report.Items) c.Log($"{(i.FoundAt is null ? "MISSING" : "found  ")} {i.Name}   ({i.JsonPath}){(i.FoundAt is null ? "" : "  " + i.FoundAt)}");
        foreach (var u in report.Unlisted.Take(20)) c.Log($"unlisted {u}");

        var outputs = new Dictionary<string, string>
        {
            [c.Str("into", "PLUGINS_MISSING")] = report.Missing.ToString(),
            ["PLUGINS_LISTED"] = report.Items.Count.ToString()
        };
        if (report.Items.Count == 0) return ToolResult.Fail("no assembly names found in the appsettings file (name the config path with 'key')");
        return report.Missing == 0
            ? ToolResult.Success($"all {report.Items.Count} listed assemblies found; {report.Unlisted.Count} unlisted beside them", outputs)
            : ToolResult.Fail($"{report.Missing} of {report.Items.Count} listed assemblies missing: {string.Join(", ", report.Items.Where(i => i.FoundAt is null).Select(i => i.Name))}");
    }

    private static string Slash(string p) => p.Replace('\\', '/').TrimEnd('/');
    private static string DirOf(string p) => p[..p.LastIndexOf('/')];
    private static string NameOf(string p) => p[(p.LastIndexOf('/') + 1)..];

    private static async Task<ToolResult> Publish(ToolContext c)
    {
        var project = Slash(c.Str("project"));
        var dir = Slash(c.Str("dir"));
        var appsettings = Slash(c.Str("appsettings"));
        var key = c.Str("key", "Plugins:Assemblies");
        var configuration = c.Str("configuration", "Debug");

        // 1. the project file and its assembly name
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var found = await c.Host.FindFilesAsync(project, ["*.csproj"], 0, c.Ct);
            if (found.Count != 1) return ToolResult.Fail($"expected exactly one .csproj in {project}, found {found.Count}; name the project file");
            project = found[0];
        }
        var csproj = await c.Host.ReadTextAsync(project, c.Ct);
        if (csproj is null) return ToolResult.Fail($"project not found: {project}");
        var assemblyName = Regex.Match(csproj, @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>") is { Success: true } am ? am.Groups[1].Value : NameOf(project)[..^".csproj".Length];
        var assemblies = c.List("assemblies") is { Count: > 0 } given ? given.Select(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).ToList() : new List<string> { assemblyName };

        // 2. build
        if (c.Bool("build", true))
        {
            var b = await c.Exec("dotnet", ["build", project, "-c", configuration, "--nologo", "-v", "q"]);
            if (b.Code != 0) return ToolResult.Fail($"dotnet build exited {b.Code}");
        }

        // 3. where the build put it: the newest <AssemblyName>.dll under bin/<configuration>
        var output = c.Opt("output") is { } o ? Slash(o) : null;
        if (output is null)
        {
            var dlls = await c.Host.FindFilesAsync($"{DirOf(project)}/bin/{configuration}", [$"{assemblyName}.dll"], 3, c.Ct);
            if (dlls.Count == 0) return ToolResult.Fail($"{assemblyName}.dll not found under {DirOf(project)}/bin/{configuration}; build it, or name the folder with 'output'");
            output = DirOf(dlls[^1]);
        }
        var built = await c.Host.FindFilesAsync(output, ["*"], 4, c.Ct);
        foreach (var a in assemblies)
            if (!built.Any(f => NameOf(f).Equals(a + ".dll", StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Fail($"{a}.dll is not in the build output {output}");

        // 4. copy what is new or different (compared by content), unless the plugin folder is the build output itself
        int copied = 0, unchanged = 0;
        if (ComposeLayout.Collapse(output).Equals(ComposeLayout.Collapse(dir), StringComparison.OrdinalIgnoreCase))
            c.Log($"the plugin folder is the build output ({dir}); nothing to copy");
        else
            foreach (var f in built)
            {
                var target = $"{dir}/{f[(output.Length + 1)..]}";
                if (await c.Host.HashAsync(f, c.Ct) == await c.Host.HashAsync(target, c.Ct)) { unchanged++; continue; }
                await c.Host.CopyFileAsync(f, target, c.Ct);
                c.Log($"copied {f[(output.Length + 1)..]}");
                copied++;
            }

        // 5. make sure appsettings lists each plugin assembly, in the style the file already uses
        var text = await c.Host.ReadTextAsync(appsettings, c.Ct);
        if (text is null) return ToolResult.Fail($"appsettings file not found: {appsettings}");
        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { return ToolResult.Fail($"{appsettings} is not valid JSON: {ex.Message}"); }
        if (root is not JsonObject node) return ToolResult.Fail($"{appsettings} is not a JSON object");

        var parts = key.Split(':');
        foreach (var p in parts[..^1])
        {
            if (node[p] is not JsonObject child) node[p] = child = new JsonObject();
            node = child;
        }
        if (node[parts[^1]] is not JsonArray list) node[parts[^1]] = list = new JsonArray();
        var existing = list.Select(ToolContext.NodeStr).OfType<string>().ToList();
        var withExtension = existing.Count > 0 && existing.All(e => e.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        static string Bare(string s) => s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
        var added = assemblies.Where(a => !existing.Any(e => Bare(e).Equals(a, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var a in added) list.Add(withExtension ? a + ".dll" : a);

        var outputs = new Dictionary<string, string> { ["PLUGINS_COPIED"] = copied.ToString(), ["PLUGINS_ADDED"] = string.Join(",", added) };
        if (added.Count > 0)
        {
            var backup = $"{DirOf(appsettings)}/.toolrunner/{NameOf(appsettings)}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
            await c.Host.WriteTextAsync(backup, text, c.Ct);
            await c.Host.WriteTextAsync(appsettings, root.ToJsonString(ConfigLoader.JsonOptions) + "\n", c.Ct);
            c.Log($"added to {key}: {string.Join(", ", added)}   (previous file saved as {backup})");
            outputs["PLUGIN_SETTINGS_BACKUP"] = backup;
        }
        else c.Log($"{key} already lists {string.Join(", ", assemblies)}");

        return ToolResult.Success($"published {string.Join(", ", assemblies)}: {copied} file(s) copied, {unchanged} unchanged, {added.Count} appsettings entr{(added.Count == 1 ? "y" : "ies")} added", outputs);
    }

    private static async Task<ToolResult> Loaded(ToolContext c)
    {
        var (id, err) = await DockerTool.Resolve(c);
        if (id is null) return ToolResult.Fail(err!);

        var names = c.List("assemblies");
        if (names.Count == 0 && c.Opt("appsettings") is { } settings)
        {
            var text = await c.Host.ReadTextAsync(settings, c.Ct);
            if (text is null) return ToolResult.Fail($"appsettings file not found: {settings}");
            names = PluginCheck.AssemblyNames(text, c.Opt("key")).Select(n => n.Name).ToList();
        }
        if (names.Count == 0) return ToolResult.Fail("no assemblies to look for: give 'assemblies', or 'appsettings' (and 'key')");

        var a = new List<string> { "logs" };
        if ((c.Opt("since") ?? c.Var("STARTED_AT_UTC")) is { } since) { a.Add("--since"); a.Add(since); }
        a.Add(id);
        var r = await c.Exec("docker", a, echo: false);
        if (r.Code != 0) return ToolResult.Fail($"docker logs exited {r.Code}: {r.Stderr.Trim()}");
        var lines = r.Combined.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        var problems = new List<string>();
        foreach (var n in names.Select(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n))
        {
            var mentions = lines.Where(l => l.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
            var errors = mentions.Where(l => LogLens.Level(l) == "error").ToList();
            if (errors.Count > 0) { c.Log($"ERROR   {n}: {errors[0].Trim()}"); problems.Add($"{n} (error)"); }
            else if (mentions.Count == 0) { c.Log($"SILENT  {n}: not mentioned in {lines.Count} log line(s)"); if (!c.Bool("allowSilent", false)) problems.Add($"{n} (not mentioned)"); }
            else c.Log($"ok      {n}: {mentions[0].Trim()}");
        }
        return problems.Count == 0
            ? ToolResult.Success($"{names.Count} plugin assembl{(names.Count == 1 ? "y" : "ies")} mentioned without errors in {lines.Count} log line(s)")
            : ToolResult.Fail($"plugins not confirmed loaded: {string.Join(", ", problems)}");
    }
}
