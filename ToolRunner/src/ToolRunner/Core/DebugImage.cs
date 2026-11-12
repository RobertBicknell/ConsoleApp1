using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Core;

/// One FROM of a Dockerfile. Image has the ARG defaults declared above it expanded; Index is the line (0-based).
public sealed record DockerStage(int Index, string Image, string? Alias, string Flags);

public sealed record DebugDockerfileOptions(
    string BaseImage,
    bool AsRoot = true,
    bool Packages = true,
    string Configuration = "Debug",
    string? VsdbgContextDir = null,                      // a folder inside the build context to COPY to /vsdbg
    string? VsdbgUrl = null);                            // else: download with this script at build time; null = vsdbg is bind-mounted at run time

/// Reading a Dockerfile and deriving a debuggable variant of it. Text in, text out: nothing here touches a disk.
public static class Dockerfiles
{
    private static readonly Regex From = new(@"^\s*FROM\s+((?:--\S+\s+)*)(\S+)(?:\s+AS\s+(\S+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArgDefault = new(@"^\s*ARG\s+([A-Za-z_]\w*)=(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string[] Lines(string text) => text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    public static List<DockerStage> Stages(string text)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        var stages = new List<DockerStage>();
        var lines = Lines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            if (ArgDefault.Match(lines[i]) is { Success: true } a) args[a.Groups[1].Value] = a.Groups[2].Value.Trim('"', '\'');
            else if (From.Match(lines[i]) is { Success: true } f)
                stages.Add(new DockerStage(i, ComposeLayout.Expand(Regex.Replace(f.Groups[2].Value, @"\$([A-Za-z_]\w*)", m => "${" + m.Groups[1].Value + "}"), args), f.Groups[3].Success ? f.Groups[3].Value : null, f.Groups[1].Value));
        }
        return stages;
    }

    /// The stages the final image is made of, final stage first: `FROM base AS final` leads back to `FROM image AS base`.
    /// The last one names the external image the container actually runs on.
    public static List<DockerStage> RuntimeChain(IReadOnlyList<DockerStage> stages)
    {
        var chain = new List<DockerStage>();
        for (var s = stages.LastOrDefault(); s is not null;)
        {
            chain.Add(s);
            var cur = s;
            s = stages.LastOrDefault(p => p.Index < cur.Index && p.Alias is not null && p.Alias.Equals(cur.Image, StringComparison.OrdinalIgnoreCase));
        }
        return chain;
    }

    /// `dotnet X.dll` from ENTRYPOINT / CMD: the assembly the container runs (without .dll); null if not found.
    public static string? EntryAssembly(string text) =>
        Lines(text).Where(l => Regex.IsMatch(l, @"^\s*(ENTRYPOINT|CMD)\b", RegexOptions.IgnoreCase))
            .Select(l => Regex.Match(l, @"([\w.\-]+)\.dll")).LastOrDefault(m => m.Success)?.Groups[1].Value;

    /// Project files a COPY names, as written (relative to the build context): what tells you where the context is.
    public static List<string> CopiedProjects(string text) =>
        Lines(text).Where(l => Regex.IsMatch(l, @"^\s*COPY\b", RegexOptions.IgnoreCase))
            .SelectMany(l => Regex.Matches(l, @"[\w.\-/\\]+\.csproj").Select(m => m.Value.Replace('\\', '/'))).Distinct().ToList();

    /// Where the sources sit inside the build stage: the WORKDIR in effect at `COPY . .` (what the .pdb files record).
    public static string? SourceRoot(string text)
    {
        string? workdir = null;
        foreach (var l in Lines(text))
        {
            if (From.IsMatch(l)) workdir = null;
            else if (Regex.Match(l, @"^\s*WORKDIR\s+(\S+)", RegexOptions.IgnoreCase) is { Success: true } w)
                workdir = w.Groups[1].Value.StartsWith('/') ? w.Groups[1].Value : $"{workdir?.TrimEnd('/')}/{w.Groups[1].Value}";
            else if (Regex.Match(l, @"^\s*COPY\s+\.\s+(\S+)\s*$", RegexOptions.IgnoreCase) is { Success: true } c)
            {
                var dest = c.Groups[1].Value.TrimEnd('/');
                return dest is "." or "" ? workdir : dest.StartsWith('/') ? dest : $"{workdir?.TrimEnd('/')}/{dest.TrimStart('.', '/')}";
            }
        }
        return null;
    }

    /// The Dockerfile with its runtime base swapped for `BaseImage` and the debugging pieces added right after that FROM:
    /// root, ps/curl/unzip, vsdbg in /vsdbg, diagnostics on. A Debug build replaces a Release one. Later USER lines in the
    /// runtime stages are commented out so the process stays root. Everything else is left exactly as written.
    public static (string Text, List<string> Changes) DebugVariant(string text, DebugDockerfileOptions o)
    {
        var lines = Lines(text).ToList();
        var stages = Stages(text);
        if (stages.Count == 0) throw new InvalidOperationException("no FROM in the Dockerfile");
        var chain = RuntimeChain(stages);
        var root = chain[^1];
        var changes = new List<string>();

        if (o.AsRoot)
            foreach (var s in chain)
            {
                var end = stages.FirstOrDefault(n => n.Index > s.Index)?.Index ?? lines.Count;
                for (var i = s.Index + 1; i < end; i++)
                    if (Regex.IsMatch(lines[i], @"^\s*USER\s+(?!root\b|0\b)", RegexOptions.IgnoreCase))
                    {
                        changes.Add($"line {i + 1}: commented out `{lines[i].Trim()}` (the debug image runs as root)");
                        lines[i] = "# (debug image) " + lines[i];
                    }
            }

        if (!string.IsNullOrEmpty(o.Configuration))
            for (var i = 0; i < lines.Count; i++)
            {
                var changed = Regex.Replace(lines[i], @"(?<=(?:\s-c|--configuration)[ =])Release\b", o.Configuration);
                changed = Regex.Replace(changed, @"(?i)(?<=^\s*ARG\s+\w*configuration\w*=)Release\b", o.Configuration);
                if (changed != lines[i]) { changes.Add($"line {i + 1}: Release -> {o.Configuration}"); lines[i] = changed; }
            }

        var block = new List<string> { "", "# ---- added by toolrunner: debugging only, never ship this image ----" };
        if (o.AsRoot) block.Add("USER root");
        if (o.Packages)
            block.Add("RUN if command -v apt-get >/dev/null 2>&1; then apt-get update && apt-get install -y --no-install-recommends procps curl unzip ca-certificates && rm -rf /var/lib/apt/lists/*; "
                    + "elif command -v apk >/dev/null 2>&1; then apk add --no-cache procps curl unzip bash; "
                    + "elif command -v microdnf >/dev/null 2>&1; then microdnf install -y procps-ng unzip && microdnf clean all; "
                    + "elif command -v dnf >/dev/null 2>&1; then dnf install -y procps-ng unzip && dnf clean all; "
                    + "else echo 'toolrunner: no known package manager in this base image; ps/curl/unzip not installed'; fi");
        if (o.VsdbgContextDir is { } dir) { block.Add($"COPY {dir.TrimEnd('/')}/ /vsdbg/"); block.Add("RUN chmod +x /vsdbg/vsdbg"); }
        else if (o.VsdbgUrl is { Length: > 0 } url) block.Add($"RUN curl -sSL {url} | sh /dev/stdin -v latest -l /vsdbg");
        block.Add("ENV DOTNET_EnableDiagnostics=1");
        block.Add("# ---- end of toolrunner additions ----");
        block.Add("");

        changes.Add($"line {root.Index + 1}: base image {root.Image} -> {o.BaseImage}");
        lines[root.Index] = $"FROM {root.Flags}{o.BaseImage}{(root.Alias is null ? "" : " AS " + root.Alias)}";
        lines.InsertRange(root.Index + 1, block);
        changes.Add(o.VsdbgContextDir is not null ? $"vsdbg copied in from the build context ({o.VsdbgContextDir})"
                  : o.VsdbgUrl is { Length: > 0 } ? $"vsdbg downloaded at build time from {o.VsdbgUrl}" : "vsdbg is not in the image: it is bind-mounted when the service starts");

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return ("# GENERATED by toolrunner from the Dockerfile beside it. Debugging only." + nl + string.Join(nl, lines), changes);
    }
}

public sealed record ImageRef(string Repo, string Tag)
{
    /// "host:5000/a/b:tag@sha256:..." -> (host:5000/a/b, tag). No tag means "latest".
    public static ImageRef Parse(string image)
    {
        var s = image.Split('@')[0];
        var colon = s.LastIndexOf(':');
        return colon > s.LastIndexOf('/') ? new(s[..colon], s[(colon + 1)..]) : new(s, "latest");
    }
    public string Name => Repo[(Repo.LastIndexOf('/') + 1)..];
    public override string ToString() => $"{Repo}:{Tag}";
}

public sealed record ImageCandidate(string Image, int Score, string Why);

/// Choosing an unhardened base image to stand in for a hardened one.
public static class DebugImages
{
    private static readonly Regex Hardened = new(@"\bdhi\b|harden|chisel|distroless|scratch|\bfips\b|-extra\b|nonroot|rootless|minimal|\bslim-secure\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Kind(string image) =>
        Regex.IsMatch(image, "aspnet", RegexOptions.IgnoreCase) ? "aspnet" : Regex.IsMatch(image, @"\bsdk\b", RegexOptions.IgnoreCase) ? "sdk"
        : Regex.IsMatch(image, "runtime", RegexOptions.IgnoreCase) ? "runtime" : "";

    public static string? Version(string image) => Regex.Match(ImageRef.Parse(image).Tag, @"\d+\.\d+") is { Success: true } m ? m.Value : null;

    /// Candidates for replacing `hardened`, best first. A candidate must be the same kind of image (aspnet / runtime)
    /// and the same .NET version when those can be read from the names; names that look hardened are left out.
    /// Preferred: the same distro family as the original (alpine stays alpine, because binaries published for musl
    /// do not run on glibc), then the plainest tag.
    public static List<ImageCandidate> Rank(string hardened, IEnumerable<string> available)
    {
        var kind = Kind(hardened);
        var version = Version(hardened);
        var alpine = hardened.Contains("alpine", StringComparison.OrdinalIgnoreCase);
        var result = new List<ImageCandidate>();
        var original = ImageRef.Parse(hardened);
        foreach (var image in available.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ImageRef.Parse(image) is { } same && same.Repo.Equals(original.Repo, StringComparison.OrdinalIgnoreCase) && same.Tag != original.Tag
                && Regex.IsMatch(same.Tag, @"(^|[-_.])dev($|[-_.])") && Regex.Replace(same.Tag, @"[-_.]?dev", "") == original.Tag)
            { result.Add(new ImageCandidate(image, 1000, "the -dev variant of the same hardened image (shell, package manager, root)")); continue; }
            if (image.Equals(hardened, StringComparison.OrdinalIgnoreCase) || Hardened.IsMatch(image)) continue;
            var r = ImageRef.Parse(image);
            if (r.Tag is "latest" or "<none>") continue;
            var k = Kind(r.Repo);
            if (k == "sdk" || (kind.Length > 0 && k != kind) || (kind.Length == 0 && k.Length == 0)) continue;
            if (version is not null && Version(image) != version) continue;
            var sameFamily = image.Contains("alpine", StringComparison.OrdinalIgnoreCase) == alpine;
            var plain = r.Tag.Split('-', '_').Length;
            result.Add(new ImageCandidate(image, (sameFamily ? 100 : 0) + (version is not null ? 50 : 0) - plain * 5 - r.Tag.Length,
                $"{k}{(version is null ? "" : " " + version)}, {(sameFamily ? "same" : "different")} distro family ({(alpine ? "alpine" : "not alpine")} original)"));
        }
        return result.OrderByDescending(c => c.Score).ThenBy(c => c.Image, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// Which compose service a project is the source of, by name: the entry assembly, project or folder name against
    /// the service name and the last part of its image name. Returns every service with its score, best first; 0 = no match.
    public static List<(ServiceInfo Service, int Score)> MatchService(IEnumerable<ServiceInfo> services, IEnumerable<string?> projectNames)
    {
        var names = projectNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Norm(n!)).Where(n => n.Length > 2).Distinct().ToList();
        int Score(string key) => key.Length < 3 ? 0 : names.Max(n => n == key ? 3 : n.EndsWith(key) || key.EndsWith(n) ? 2 : n.Contains(key) || key.Contains(n) ? 1 : 0);
        return services.Select(s => (s, names.Count == 0 ? 0 : Math.Max(Score(Norm(s.Name)), s.Image is null ? 0 : Score(Norm(ImageRef.Parse(s.Image).Name)))))
            .OrderByDescending(x => x.Item2).ToList();
    }

    // ---- the compose override file: one generated file, one block per service that currently runs a debug image

    public const string OverrideHeader = "# GENERATED by toolrunner (debugimage). Use AFTER the original file: -f docker-compose.yml -f <this file>.";

    public static SortedDictionary<string, List<string>> ParseOverride(string? text)
    {
        var blocks = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? cur = null;
        foreach (var l in (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (Regex.Match(l, @"^  ([\w.\-]+):\s*$") is { Success: true } m) blocks[m.Groups[1].Value] = cur = new();
            else if (l.StartsWith("    ") && cur is not null) cur.Add(l);
        }
        return blocks;
    }

    public static string RenderOverride(SortedDictionary<string, List<string>> blocks) =>
        string.Join("\n", new[] { OverrideHeader, "services:" }.Concat(blocks.SelectMany(b => new[] { $"  {b.Key}:" }.Concat(b.Value)))) + "\n";

    public static List<string> OverrideBlock(string image, bool asRoot, bool writableRoot, string? vsdbgMount = null)
    {
        var b = new List<string> { $"    image: {image}", "    pull_policy: never" };
        if (asRoot) b.Add("    user: root");
        if (writableRoot) b.Add("    read_only: false");
        b.Add("    environment:");
        b.Add("      DOTNET_EnableDiagnostics: \"1\"");
        if (vsdbgMount is { Length: > 0 }) { b.Add("    volumes:"); b.Add($"      - \"{vsdbgMount}:/remote_debugger:ro\""); }
        return b;
    }

    /// The lines of one service in a compose file (comments dropped), for spotting settings that get in a debugger's way.
    public static List<string> ServiceBody(string composeText, string service)
    {
        var lines = composeText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        var start = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s+{Regex.Escape(service)}:\s*$"));
        if (start < 0) return new();
        var indent = lines[start].Length - lines[start].TrimStart().Length;
        return lines.Skip(start + 1).TakeWhile(l => l.Length - l.TrimStart().Length > indent).ToList();
    }

    /// The file Visual Studio's `DebugAdapterHost.Launch /LaunchJson:` takes: vsdbg is started inside the container
    /// through `docker exec` (or ssh + docker exec for a remote host) and attaches to the process.
    public static string LaunchJson(string container, string? sshTarget, string? sourceInContainer, string? sourceOnDisk, string vsdbgPath = "/remote_debugger/vsdbg", int processId = 1, string engine = "docker")
    {
        var exec = $"exec -i {container} {vsdbgPath} --interpreter=vscode";
        var config = new JsonObject { ["name"] = $"attach to {container}", ["type"] = "coreclr", ["request"] = "attach", ["processId"] = processId, ["justMyCode"] = false };
        if (sourceInContainer is not null && sourceOnDisk is not null) config["sourceFileMap"] = new JsonObject { [sourceInContainer] = sourceOnDisk.Replace('/', '\\') };
        return new JsonObject
        {
            ["version"] = "0.2.0",
            ["adapter"] = sshTarget is null ? (engine.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? engine : engine + ".exe") : "ssh.exe",
            ["adapterArgs"] = sshTarget is null ? exec : $"-T {sshTarget} {engine} {exec}",
            ["configurations"] = new JsonArray(config)
        }.ToJsonString(ConfigLoader.JsonOptions) + "\n";
    }
}

/// Lists images in a Docker registry over the v2 API. For Artifactory, `api` is
/// https://HOST/artifactory/api/docker/REPO-KEY (so that `{api}/v2/_catalog` answers).
public static class RegistryClient
{
    /// "prefix/repo:tag" for every tag of every repository whose name matches `repoFilter` (at most `maxRepos` of them).
    /// The tags of one repository; null (with a note) when the registry does not answer.
    public static async Task<(List<string>? Tags, string? Note)> TagsAsync(string api, string repo, string? user, string? token, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(user)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
        else if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var url = $"{api.TrimEnd('/')}/v2/{repo}/tags/list";
        try
        {
            using var r = await http.GetAsync(url, ct);
            if (!r.IsSuccessStatusCode) return (null, $"{url}: HTTP {(int)r.StatusCode}");
            return ((JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["tags"] as JsonArray)?.Select(t => t!.GetValue<string>()).ToList() ?? new(), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return (null, $"{url}: {e.Message}"); }
    }

    public static async Task<(List<string> Images, List<string> Notes)> ListAsync(string api, string? prefix, string? user, string? token,
        Regex repoFilter, int maxRepos, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var images = new List<string>();
        var notes = new List<string>();
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(user)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
        else if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        api = api.TrimEnd('/');

        async Task<JsonNode?> Get(string url)
        {
            try
            {
                using var r = await http.GetAsync(url, ct);
                if (!r.IsSuccessStatusCode) { notes.Add($"{url}: HTTP {(int)r.StatusCode}"); return null; }
                return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { notes.Add($"{url}: {e.Message}"); return null; }
        }

        var repos = ((await Get($"{api}/v2/_catalog?n=10000"))?["repositories"] as JsonArray)?.Select(n => n!.GetValue<string>()).Where(r => repoFilter.IsMatch(r)).ToList() ?? new();
        if (repos.Count > maxRepos) { notes.Add($"{repos.Count} repositories match; only the first {maxRepos} were read"); repos = repos.Take(maxRepos).ToList(); }
        foreach (var repo in repos)
            if ((await Get($"{api}/v2/{repo}/tags/list"))?["tags"] is JsonArray tags)
                images.AddRange(tags.Select(t => $"{(string.IsNullOrEmpty(prefix) ? "" : prefix.TrimEnd('/') + "/")}{repo}:{t!.GetValue<string>()}"));
        return (images, notes);
    }
}
