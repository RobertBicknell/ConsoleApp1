using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Core;

// ---- what the exploration brings back (plain data: saved as JSON, shown in the UI, read by the assessment)

/// Kind: plugins (.NET projects), config (compose / product.json), tests (a project that references Selenium), unknown.
public sealed record AzdoRepo(string Name, string Id, string DefaultBranch, List<string> Branches, List<string> Tags, List<string> Files, string Kind, List<string> Evidence);
public sealed record AzdoAgent(string Name, string Status, bool Enabled, string Os, Dictionary<string, string> Capabilities);
public sealed record AzdoPool(string Name, int Id, bool Hosted, List<AzdoAgent> Agents);
public sealed record AzdoNamed(string Name, string Detail);
public sealed record AzdoFindings(string Url, string? ApiVersion, List<string> Projects, string? Project, List<AzdoRepo> Repos, List<AzdoPool> Pools,
    List<AzdoNamed> ServiceConnections, List<string> VariableGroups, List<string> Extensions, List<string> Tasks, List<AzdoNamed> Pipelines,
    List<string> BuildNumbers, List<string> Notes)
{
    public static AzdoFindings Empty(string url, params string[] notes) => new(url, null, [], null, [], [], [], [], [], [], [], [], notes.ToList());
}

public sealed record JfrogRepo(string Key, string Type, string PackageType);
/// NugetVersions: package id -> versions, per repository key ("repo/id"). DockerTags: "repo/image" -> tags.
public sealed record JfrogFindings(string Url, List<JfrogRepo> Repos, Dictionary<string, List<string>> NugetVersions, Dictionary<string, List<string>> DockerTags, List<string> Notes);

public sealed record Exploration(DateTimeOffset At, AzdoFindings Azdo, JfrogFindings Jfrog);

/// One authenticated JSON reader for a REST server. Failures become notes, never exceptions: an exploration reports
/// what it could not see instead of stopping at the first closed door.
public sealed class RestReader : IDisposable
{
    private readonly HttpClient _http;
    public List<string> Notes { get; } = new();

    /// user + secret = basic auth; secret alone = `bearer` (JFrog) or basic with an empty user (an Azure DevOps PAT);
    /// neither = the signed-in Windows account (on-prem Azure DevOps usually accepts it).
    public RestReader(string? user, string? secret, bool secretIsPat, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { UseDefaultCredentials = string.IsNullOrEmpty(secret) }) { Timeout = TimeSpan.FromSeconds(40) };
        if (!string.IsNullOrEmpty(user) || (secretIsPat && !string.IsNullOrEmpty(secret)))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
        else if (!string.IsNullOrEmpty(secret)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var r = await _http.SendAsync(req, ct);
            return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (0, e.Message); }
    }

    public async Task<JsonNode?> GetAsync(string url, CancellationToken ct, bool quiet = false)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, url, null, ct);
        if (status is >= 200 and < 300)
            try { return JsonNode.Parse(body); } catch (JsonException) { if (!quiet) Notes.Add($"{url}: the answer is not JSON (a sign-in page? check the URL and the credentials)"); return null; }
        if (!quiet) Notes.Add($"{url}: {(status == 0 ? body : "HTTP " + status + (status is 401 or 403 ? " (not allowed with these credentials)" : ""))}");
        return null;
    }

    public async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, url, null, ct);
        return status is >= 200 and < 300 ? body : null;
    }

    public void Dispose() => _http.Dispose();
}

/// Reads an Azure DevOps Server collection (on-prem) or organisation. Everything here is a GET.
public static class AzdoExplorer
{
    private static readonly string[] ApiVersions = ["7.1", "7.0", "6.0", "5.1", "5.0"];
    private static readonly Regex Interesting = new(@"(\.sln|\.csproj|\.nuspec|nuget\.config|Dockerfile[^/]*|docker-compose[^/]*\.ya?ml|compose\.ya?ml|\.env|product\.json|appsettings[^/]*\.json|azure-pipelines[^/]*\.ya?ml|Directory\.Build\.props|global\.json|GitVersion\.yml|version\.(json|txt))$", RegexOptions.IgnoreCase);
    private static readonly Regex Capability = new(@"^(Agent\.(OS|OSArchitecture|Version|Name)|docker.*|podman.*|dotnet.*|DotNetFramework.*|MSBuild.*|VSTest.*|VisualStudio.*|node.*|npm|java.*|git|curl|bash|sh|PowerShell|pwsh|chrome.*|google-chrome|firefox|jfrog|jf|nuget)$", RegexOptions.IgnoreCase);

    private static string S(JsonNode? n) => ToolContext.NodeStr(n) ?? "";
    private static IEnumerable<JsonNode> Values(JsonNode? n) => (n?["value"] as JsonArray ?? []).OfType<JsonNode>();

    public static async Task<AzdoFindings> RunAsync(string url, string? project, string? pat, CancellationToken ct, HttpMessageHandler? handler = null, Action<string>? progress = null)
    {
        url = url.TrimEnd('/');
        using var rest = new RestReader(null, pat, secretIsPat: true, handler);
        void Say(string s) => progress?.Invoke(s);

        // which REST version this server speaks: the newest one that answers
        string? api = null; JsonNode? projectsNode = null;
        foreach (var v in ApiVersions)
            if ((projectsNode = await rest.GetAsync($"{url}/_apis/projects?api-version={v}", ct, quiet: true)) is not null) { api = v; break; }
        if (api is null)
        {
            await rest.GetAsync($"{url}/_apis/projects?api-version={ApiVersions[^1]}", ct);     // once more, for the reason
            return AzdoFindings.Empty(url, [.. rest.Notes, "No REST version answered. The URL must be the COLLECTION, e.g. https://server/tfs/DefaultCollection or https://server/DefaultCollection."]);
        }
        Say($"connected; REST api-version {api}");
        var projects = Values(projectsNode).Select(p => S(p["name"])).Order(StringComparer.OrdinalIgnoreCase).ToList();
        project = projects.FirstOrDefault(p => p.Equals(project, StringComparison.OrdinalIgnoreCase)) ?? (projects.Count == 1 ? projects[0] : null);

        Task<JsonNode?> Get(string path, string query = "", string? version = null) =>
            rest.GetAsync($"{url}/{path}?{(query.Length > 0 ? query + "&" : "")}api-version={version ?? api}", ct);

        // collection-wide: where builds can run, and what they can use
        var pools = new List<AzdoPool>();
        foreach (var p in Values(await Get("_apis/distributedtask/pools")))
        {
            var id = p["id"]!.GetValue<int>();
            var agents = Values(await Get($"_apis/distributedtask/pools/{id}/agents", "includeCapabilities=true")).Select(a =>
            {
                var caps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kind in new[] { "systemCapabilities", "userCapabilities" })
                    foreach (var (k, v) in a[kind] as JsonObject ?? []) if (Capability.IsMatch(k)) caps[k] = S(v);
                return new AzdoAgent(S(a["name"]), S(a["status"]), a["enabled"]?.GetValue<bool>() ?? false, caps.GetValueOrDefault("Agent.OS", S(a["osDescription"])), caps);
            }).ToList();
            pools.Add(new AzdoPool(S(p["name"]), id, p["isHosted"]?.GetValue<bool>() ?? false, agents));
        }
        Say($"{pools.Count} agent pool(s), {pools.Sum(p => p.Agents.Count)} agent(s)");
        var extensions = Values(await Get("_apis/extensionmanagement/installedextensions", version: api + "-preview.1"))
            .Select(e => $"{S(e["publisherId"])}.{S(e["extensionId"])} {S(e["version"])}").Order().ToList();
        var tasks = Values(await Get("_apis/distributedtask/tasks")).Select(t => $"{S(t["name"])}@{S(t["version"]?["major"])}").Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        Say($"{extensions.Count} extension(s), {tasks.Count} task(s)");

        var repos = new List<AzdoRepo>();
        var connections = new List<AzdoNamed>(); var groups = new List<string>(); var pipelines = new List<AzdoNamed>(); var builds = new List<string>();
        if (project is null) rest.Notes.Add($"No project chosen (AZDO_PROJECT); the collection has: {string.Join(", ", projects)}. Repositories, pipelines and connections were not read.");
        else
        {
            var pe = Uri.EscapeDataString(project);
            foreach (var r in Values(await Get($"{pe}/_apis/git/repositories")))
            {
                var (name, id) = (S(r["name"]), S(r["id"]));
                async Task<List<string>> Refs(string filter) =>
                    Values(await Get($"{pe}/_apis/git/repositories/{id}/refs", $"filter={filter}")).Select(x => S(x["name"])[("refs/" + filter).Length..]).ToList();
                var files = Values(await Get($"{pe}/_apis/git/repositories/{id}/items", "scopePath=/&recursionLevel=Full"))
                    .Where(i => i["isFolder"]?.GetValue<bool>() != true).Select(i => S(i["path"])).Where(p => p.Count(c => c == '/') <= 5 && Interesting.IsMatch(p)).Order().ToList();

                // what kind of repository is it: look inside a few project files for a Selenium reference
                var evidence = new List<string>();
                var selenium = false;
                foreach (var csproj in files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Take(12))
                    if ((await rest.GetTextAsync($"{url}/{pe}/_apis/git/repositories/{id}/items?path={Uri.EscapeDataString(csproj)}&api-version={api}&$format=text", ct))?.Contains("Selenium", StringComparison.OrdinalIgnoreCase) == true)
                    { selenium = true; evidence.Add($"{csproj} references Selenium"); break; }
                var compose = files.FirstOrDefault(f => Regex.IsMatch(f, @"(docker-)?compose[^/]*\.ya?ml$", RegexOptions.IgnoreCase));
                var product = files.FirstOrDefault(f => f.EndsWith("product.json", StringComparison.OrdinalIgnoreCase));
                var projectsIn = files.Count(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
                if (compose is not null) evidence.Add($"compose file {compose}");
                if (product is not null) evidence.Add($"product file {product}");
                if (projectsIn > 0) evidence.Add($"{projectsIn} .NET project(s)");
                var kind = selenium ? "tests" : compose is not null || product is not null ? "config" : projectsIn > 0 ? "plugins" : "unknown";
                repos.Add(new AzdoRepo(name, id, S(r["defaultBranch"]).Replace("refs/heads/", ""), await Refs("heads/"), await Refs("tags/"), files, kind, evidence));
                Say($"repository {name}: {kind}");
            }
            connections = Values(await Get($"{pe}/_apis/serviceendpoint/endpoints", version: api + "-preview")).Select(e => new AzdoNamed(S(e["name"]), $"{S(e["type"])} {S(e["url"])}".Trim())).ToList();
            groups = Values(await Get($"{pe}/_apis/distributedtask/variablegroups", version: api + "-preview")).Select(g => $"{S(g["name"])} ({string.Join(", ", (g["variables"] as JsonObject ?? []).Select(v => v.Key))})").ToList();
            pipelines = Values(await Get($"{pe}/_apis/build/definitions", "includeAllProperties=true")).Select(d => new AzdoNamed(S(d["name"]),
                $"{(S(d["process"]?["yamlFilename"]) is { Length: > 0 } y ? "yaml " + y : "classic")} on {S(d["repository"]?["name"])}, pool {S(d["queue"]?["name"])}")).ToList();
            builds = Values(await Get($"{pe}/_apis/build/builds", "$top=30")).Select(b => $"{S(b["definition"]?["name"])}: {S(b["buildNumber"])} ({S(b["sourceBranch"]).Replace("refs/heads/", "").Replace("refs/tags/", "tag ")}, {S(b["result"])})").ToList();
            Say($"{connections.Count} service connection(s), {groups.Count} variable group(s), {pipelines.Count} pipeline(s)");
        }
        return new AzdoFindings(url, api, projects, project, repos, pools, connections, groups, extensions, tasks, pipelines, builds, rest.Notes);
    }

    /// Registers a YAML pipeline that already exists in a repository. The only write in this file.
    public static async Task<(bool Ok, string Message)> CreatePipelineAsync(string url, string project, string? pat, string apiVersion, string name, string repoId, string repoName, string yamlPath,
        CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var rest = new RestReader(null, pat, secretIsPat: true, handler);
        var body = new JsonObject
        {
            ["name"] = name, ["folder"] = "\\",
            ["configuration"] = new JsonObject { ["type"] = "yaml", ["path"] = yamlPath, ["repository"] = new JsonObject { ["id"] = repoId, ["name"] = repoName, ["type"] = "azureReposGit" } }
        };
        var (status, text) = await rest.SendAsync(HttpMethod.Post, $"{url.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_apis/pipelines?api-version={apiVersion}-preview.1", body.ToJsonString(), ct);
        return status is >= 200 and < 300 ? (true, $"created pipeline '{name}' for {repoName}{yamlPath}") : (false, $"HTTP {status}: {(text.Length > 300 ? text[..300] : text)}");
    }
}

/// Reads Artifactory: which repositories exist, and the versions inside the NuGet and Docker ones.
public static class JfrogExplorer
{
    /// url: https://HOST/artifactory. nugetRepos / dockerRepos: repository keys to look inside (others are only listed).
    public static async Task<JfrogFindings> RunAsync(string url, string? user, string? token, IEnumerable<string> nugetRepos, IEnumerable<string> dockerRepos, CancellationToken ct,
        HttpMessageHandler? handler = null, Action<string>? progress = null)
    {
        url = url.TrimEnd('/');
        using var rest = new RestReader(user, token, secretIsPat: false, handler);
        var repos = (await rest.GetAsync($"{url}/api/repositories", ct) as JsonArray ?? [])
            .Select(r => new JfrogRepo(ToolContext.NodeStr(r?["key"]) ?? "", ToolContext.NodeStr(r?["type"]) ?? "", ToolContext.NodeStr(r?["packageType"]) ?? "")).OrderBy(r => r.Key).ToList();
        progress?.Invoke($"artifactory: {repos.Count} repositories");

        async Task<List<(string Name, bool Folder)>> Children(string path) =>
            ((await rest.GetAsync($"{url}/api/storage/{path}", ct))?["children"] as JsonArray ?? [])
                .Select(c => ((ToolContext.NodeStr(c?["uri"]) ?? "").TrimStart('/'), c?["folder"]?.GetValue<bool>() ?? false)).ToList();

        // NuGet: packages are stored as files named <id>.<version>.nupkg, flat or one folder per id
        var nuget = new Dictionary<string, List<string>>();
        var nupkg = new Regex(@"^(?<id>.+?)\.(?<v>\d+(\.\d+){1,3}(-[0-9A-Za-z.\-]+)?)\.nupkg$");
        foreach (var repo in nugetRepos.Where(r => r.Length > 0).Distinct())
        {
            var top = await Children(repo);
            var files = top.Where(c => !c.Folder).Select(c => c.Name).ToList();
            foreach (var folder in top.Where(c => c.Folder).Take(60))
            {
                var inner = await Children($"{repo}/{folder.Name}");
                files.AddRange(inner.Where(c => !c.Folder).Select(c => c.Name));
                foreach (var deeper in inner.Where(c => c.Folder).Take(40)) files.AddRange((await Children($"{repo}/{folder.Name}/{deeper.Name}")).Where(c => !c.Folder).Select(c => c.Name));
            }
            foreach (var m in files.Select(f => nupkg.Match(f)).Where(m => m.Success))
            {
                var key = $"{repo}/{m.Groups["id"].Value}";
                if (!nuget.TryGetValue(key, out var list)) nuget[key] = list = new();
                if (!list.Contains(m.Groups["v"].Value)) list.Add(m.Groups["v"].Value);
            }
            progress?.Invoke($"artifactory {repo}: {nuget.Keys.Count(k => k.StartsWith(repo + "/"))} package(s)");
        }

        // Docker: the registry API of each repository
        var docker = new Dictionary<string, List<string>>();
        foreach (var repo in dockerRepos.Where(r => r.Length > 0).Distinct())
        {
            var catalog = (await rest.GetAsync($"{url}/api/docker/{repo}/v2/_catalog?n=2000", ct))?["repositories"] as JsonArray ?? [];
            foreach (var image in catalog.Select(c => ToolContext.NodeStr(c)!).Take(80))
                if ((await rest.GetAsync($"{url}/api/docker/{repo}/v2/{image}/tags/list", ct))?["tags"] is JsonArray tags)
                    docker[$"{repo}/{image}"] = tags.Select(t => ToolContext.NodeStr(t)!).ToList();
            progress?.Invoke($"artifactory {repo}: {docker.Keys.Count(k => k.StartsWith(repo + "/"))} image(s)");
        }
        return new JfrogFindings(url, repos, nuget, docker, rest.Notes);
    }
}
