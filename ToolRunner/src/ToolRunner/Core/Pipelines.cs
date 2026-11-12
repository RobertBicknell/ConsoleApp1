using System.Text;
using System.Text.RegularExpressions;

namespace ToolRunner.Core;

/// One editable setting of the pipelines: the variable that holds it, what it is, and its value.
public sealed record PipelineSetting(string Var, string Label, string Help, string Value, string Group);

/// Everything the two pipelines need to be told. Each value lives in a variable (PIPELINE_*, AZDO_*, JFROG_*), so it is
/// edited in the UI, on the command line (--set) or in globals.user.json like any other.
public sealed record PipelineSettings(IReadOnlyList<PipelineSetting> All)
{
    public static readonly (string Var, string Label, string Help, string Default, string Group)[] Spec =
    [
        ("AZDO_URL", "collection URL", "https://server/tfs/DefaultCollection (on-prem) or https://dev.azure.com/org", "", "azure devops"),
        ("AZDO_PROJECT", "project", "The team project that holds the three repositories.", "", "azure devops"),
        ("AZDO_PAT", "personal access token", "Empty = sign in as the current Windows account. Kept in your own layer.", "", "azure devops"),
        ("PIPELINE_POOL", "agent pool", "Where both pipelines run. Needs dotnet; the product pipeline also needs docker with compose, bash and a browser.", "", "azure devops"),
        ("PIPELINE_PLUGINS_REPO", "plugins repository", "The .NET plugin projects: built, tested, packed and pushed as NuGet.", "", "repositories"),
        ("PIPELINE_CONFIG_REPO", "config repository", "docker-compose, .env, appsettings, product.json.", "", "repositories"),
        ("PIPELINE_TESTS_REPO", "tests repository", "The Selenium test projects run against the webportal.", "", "repositories"),
        ("PIPELINE_MAIN_BRANCH", "main branch", "Builds of this branch and of release/* run automatically.", "main", "repositories"),
        ("PIPELINE_TAG_PREFIX", "version tag prefix", "A git tag <prefix><version> marks a release: v gives v2.4.1. Empty = the tag is the version.", "v", "versions"),
        ("PIPELINE_PACKAGES", "plugin package ids", "`;`-separated ids of the NuGet packages the product pipeline fetches. Empty = every package the plugins pipeline produced cannot be known; name them.", "", "versions"),
        ("JFROG_URL", "artifactory URL", "https://HOST/artifactory", "", "jfrog"),
        ("JFROG_USER", "artifactory user", "For exploring from this machine. The pipelines read JFROG_USER / JFROG_TOKEN from the variable group.", "", "jfrog"),
        ("JFROG_TOKEN", "artifactory token", "Kept in your own layer.", "", "jfrog"),
        ("PIPELINE_CREDENTIALS_GROUP", "variable group with JFROG_USER and JFROG_TOKEN", "A library variable group in the project; the token marked secret.", "jfrog-credentials", "jfrog"),
        ("PIPELINE_NUGET_REPO_DEV", "NuGet repository, dev", "Artifactory repository key the plugins are pushed to for dev.", "", "jfrog"),
        ("PIPELINE_NUGET_REPO_PROD", "NuGet repository, prod", "The same for prod.", "", "jfrog"),
        ("PIPELINE_DOCKER_REGISTRY_DEV", "Docker registry, dev", "What precedes an image name when pushing for dev, e.g. HOST/docker-dev-local.", "", "jfrog"),
        ("PIPELINE_DOCKER_REGISTRY_PROD", "Docker registry, prod", "The same for prod.", "", "jfrog"),
        ("PIPELINE_DOCKER_REPO_DEV", "Docker repository key, dev", "Artifactory key behind the dev registry (for looking up tags).", "", "jfrog"),
        ("PIPELINE_DOCKER_REPO_PROD", "Docker repository key, prod", "The same for prod.", "", "jfrog"),
        ("PIPELINE_PRODUCT", "product name", "Names the final images: <registry>/<product>-<service>:<version>.", "product", "product"),
        ("PIPELINE_COMPOSE_FILE", "compose file in the config repo", "Path from the repository root.", "docker-compose.yml", "product"),
        ("PIPELINE_ENV_FILE", "env file in the config repo", "Path from the repository root.", ".env", "product"),
        ("PIPELINE_PLUGINS_DIR", "plugins folder in the config repo", "The bind-mount source the plugin assemblies are unpacked into before the stack starts.", "plugins", "product"),
        ("PIPELINE_BAKE_SERVICES", "services to bake", "`;`-separated services whose bind-mounted config and plugins are copied into a final image.", "", "product"),
        ("PIPELINE_HEALTH_URL", "portal URL on the agent", "Polled until it answers before the tests start, and handed to the tests as PORTAL_URL.", "http://localhost:8080/", "product"),
        ("PIPELINE_TESTS_PROJECTS", "test projects", "Glob inside the tests repository.", "**/*.csproj", "product")
    ];

    public static PipelineSettings From(Func<string, string?> var) =>
        new(Spec.Select(s => new PipelineSetting(s.Var, s.Label, s.Help, var(s.Var) is { Length: > 0 } v ? v : s.Default, s.Group)).ToList());

    public string this[string name] => All.First(s => s.Var == name).Value;
    public IEnumerable<string> List(string name) => this[name].Split(';', ',').Select(s => s.Trim()).Where(s => s.Length > 0);
}

/// Status: ok | missing | unknown (could not be determined: usually the exploration could not read it).
public sealed record PipelineCheck(string Pipeline, string Name, string Status, string Evidence, string Fix);

public sealed record VersionRow(string Version, bool PluginsTag, bool ConfigTag, bool Package, bool Image);

public static class Pipelines
{
    // ---------------------------------------------------------------- exploring, and keeping what was found

    public static string StorePath(string configDir) => Path.Combine(History.Dir(configDir), "exploration.json");

    public static Exploration? Load(string configDir)
    {
        try { return File.Exists(StorePath(configDir)) ? System.Text.Json.JsonSerializer.Deserialize<Exploration>(File.ReadAllText(StorePath(configDir)), ConfigLoader.JsonOptions) : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static void Save(string configDir, Exploration x)
    {
        Directory.CreateDirectory(History.Dir(configDir));
        File.WriteAllText(StorePath(configDir), System.Text.Json.JsonSerializer.Serialize(x, ConfigLoader.JsonOptions));
    }

    /// Reads Azure DevOps and Artifactory with the current settings. Only GETs. When the JFrog repositories are not
    /// chosen yet, the ones the first look suggests are looked into straight away.
    public static async Task<Exploration> ExploreAsync(PipelineSettings s, CancellationToken ct, Action<string>? progress = null, HttpMessageHandler? handler = null)
    {
        var azdo = s["AZDO_URL"].Length == 0 ? AzdoFindings.Empty("", "AZDO_URL is not set")
            : await AzdoExplorer.RunAsync(s["AZDO_URL"], s["AZDO_PROJECT"], s["AZDO_PAT"], ct, handler, progress);
        async Task<JfrogFindings> Jfrog(PipelineSettings with) => s["JFROG_URL"].Length == 0 ? new JfrogFindings("", [], new(), new(), ["JFROG_URL is not set"])
            : await JfrogExplorer.RunAsync(s["JFROG_URL"], s["JFROG_USER"], s["JFROG_TOKEN"], [with["PIPELINE_NUGET_REPO_DEV"], with["PIPELINE_NUGET_REPO_PROD"]],
                [with["PIPELINE_DOCKER_REPO_DEV"], with["PIPELINE_DOCKER_REPO_PROD"]], ct, handler, progress);
        var first = new Exploration(DateTimeOffset.Now, azdo, await Jfrog(s));
        var suggested = Suggest(first, s);
        if (!suggested.Keys.Any(k => k.Contains("_REPO_"))) return first;
        var with = new PipelineSettings(s.All.Select(a => suggested.TryGetValue(a.Var, out var v) ? a with { Value = v } : a).ToList());
        return first with { Jfrog = await Jfrog(with) };
    }

    // ---------------------------------------------------------------- suggestions from what was found

    /// Values the exploration makes obvious, for settings that are still empty. Never overrides what is set.
    public static Dictionary<string, string> Suggest(Exploration x, PipelineSettings s)
    {
        var found = new Dictionary<string, string>();
        void Offer(string key, string? value) { if (!string.IsNullOrEmpty(value) && s.All.First(a => a.Var == key).Value == PipelineSettings.Spec.First(p => p.Var == key).Default) found[key] = value!; }

        Offer("AZDO_PROJECT", x.Azdo.Project);
        foreach (var (kind, key) in new[] { ("plugins", "PIPELINE_PLUGINS_REPO"), ("config", "PIPELINE_CONFIG_REPO"), ("tests", "PIPELINE_TESTS_REPO") })
            if (x.Azdo.Repos.Where(r => r.Kind == kind).ToList() is [var only]) Offer(key, only.Name);
        Offer("PIPELINE_POOL", x.Azdo.Pools.Where(p => !p.Hosted).OrderByDescending(p => p.Agents.Count(a => a.Enabled && HasDocker(a))).ThenByDescending(p => p.Agents.Count(a => a.Enabled)).FirstOrDefault(p => p.Agents.Count > 0)?.Name);
        if (x.Azdo.Repos.FirstOrDefault(r => r.Kind == "config") is { } cfg && cfg.DefaultBranch.Length > 0 && cfg.DefaultBranch != "main") found["PIPELINE_MAIN_BRANCH"] = cfg.DefaultBranch;

        string? Repo(string package, string env) => x.Jfrog.Repos.Where(r => r.PackageType.Equals(package, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(r.Key, $@"(^|[-_.]){env}", RegexOptions.IgnoreCase))
            .OrderBy(r => r.Type.Equals("local", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault()?.Key;
        Offer("PIPELINE_NUGET_REPO_DEV", Repo("nuget", "dev")); Offer("PIPELINE_NUGET_REPO_PROD", Repo("nuget", "prod"));
        Offer("PIPELINE_DOCKER_REPO_DEV", Repo("docker", "dev")); Offer("PIPELINE_DOCKER_REPO_PROD", Repo("docker", "prod"));
        var host = Uri.TryCreate(x.Jfrog.Url, UriKind.Absolute, out var u) ? u.Authority : null;
        if (host is not null)
        {
            Offer("PIPELINE_DOCKER_REGISTRY_DEV", Repo("docker", "dev") is { } d ? $"{host}/{d}" : null);
            Offer("PIPELINE_DOCKER_REGISTRY_PROD", Repo("docker", "prod") is { } p ? $"{host}/{p}" : null);
        }
        if (x.Azdo.VariableGroups.FirstOrDefault(g => Regex.IsMatch(g, "jfrog|artifactory", RegexOptions.IgnoreCase)) is { } group) found["PIPELINE_CREDENTIALS_GROUP"] = group[..group.LastIndexOf(" (", StringComparison.Ordinal)];
        return found;
    }

    private static bool Has(AzdoAgent a, string pattern) => a.Capabilities.Keys.Any(k => Regex.IsMatch(k, pattern, RegexOptions.IgnoreCase));
    private static bool HasDocker(AzdoAgent a) => Has(a, "^(docker|podman)");

    // ---------------------------------------------------------------- can the pipelines be built here?

    public static List<PipelineCheck> Assess(Exploration x, PipelineSettings s)
    {
        var checks = new List<PipelineCheck>();
        void Add(string pipeline, string name, bool? ok, string evidence, string fix) => checks.Add(new(pipeline, name, ok switch { true => "ok", false => "missing", null => "unknown" }, evidence, ok == true ? "" : fix));
        var a = x.Azdo;

        Add("both", "Azure DevOps answers", a.ApiVersion is not null, a.ApiVersion is null ? string.Join("; ", a.Notes.Take(2)) : $"{a.Url}, REST {a.ApiVersion}", "Check AZDO_URL (the collection) and the credentials.");
        Add("both", "project", a.Project is not null, a.Project ?? $"projects here: {string.Join(", ", a.Projects)}", "Set AZDO_PROJECT.");

        AzdoRepo? Repo(string key, string kind, string pipeline, string label)
        {
            var named = a.Repos.FirstOrDefault(r => r.Name.Equals(s[key], StringComparison.OrdinalIgnoreCase));
            var candidates = a.Repos.Where(r => r.Kind == kind).Select(r => r.Name).ToList();
            Add(pipeline, label, named is not null, named is not null ? $"{named.Name} ({named.Kind}: {string.Join("; ", named.Evidence)}), default branch {named.DefaultBranch}"
                : candidates.Count > 0 ? $"looks like: {string.Join(", ", candidates)}" : $"no repository looks like a {kind} repository among {a.Repos.Count}", $"Set {key}.");
            if (named is not null && named.Kind != kind) Add(pipeline, $"{label} content", false, $"{named.Name} looks like a {named.Kind} repository", $"Check {key}.");
            return named;
        }
        var plugins = Repo("PIPELINE_PLUGINS_REPO", "plugins", "plugins", "plugins repository");
        var config = Repo("PIPELINE_CONFIG_REPO", "config", "product", "config repository");
        Repo("PIPELINE_TESTS_REPO", "tests", "product", "tests repository");

        if (plugins is not null)
        {
            var tests = plugins.Files.Where(f => Regex.IsMatch(f, @"Tests?[^/]*\.csproj$", RegexOptions.IgnoreCase)).ToList();
            Add("plugins", "unit test projects", tests.Count > 0, tests.Count > 0 ? string.Join(", ", tests.Take(4)) : "no *Test*.csproj found (to five folders deep)", "The test step looks for **/*Test*.csproj; adjust the YAML if the tests are named differently.");
            Add("plugins", "a solution to build", plugins.Files.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)), string.Join(", ", plugins.Files.Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)).Take(3)), "Without a .sln the YAML builds **/*.csproj.");
        }
        if (config is not null)
        {
            Add("product", "compose file in the config repository", config.Files.Any(f => f.TrimStart('/').Equals(s["PIPELINE_COMPOSE_FILE"], StringComparison.OrdinalIgnoreCase)),
                $"compose files there: {string.Join(", ", config.Files.Where(f => Regex.IsMatch(f, @"compose[^/]*\.ya?ml$", RegexOptions.IgnoreCase)))}", "Set PIPELINE_COMPOSE_FILE to its path from the repository root.");
            Add("product", "product.json", config.Files.Any(f => f.EndsWith("product.json", StringComparison.OrdinalIgnoreCase)), string.Join(", ", config.Files.Where(f => f.EndsWith("product.json", StringComparison.OrdinalIgnoreCase))), "Expected in the config repository.");
        }

        // where it runs
        var pool = a.Pools.FirstOrDefault(p => p.Name.Equals(s["PIPELINE_POOL"], StringComparison.OrdinalIgnoreCase));
        Add("both", "agent pool", pool is not null, pool is not null ? $"{pool.Name}: {pool.Agents.Count} agent(s)" : $"pools: {string.Join(", ", a.Pools.Select(p => $"{p.Name} ({p.Agents.Count})"))}", "Set PIPELINE_POOL.");
        if (pool is not null)
        {
            var live = pool.Agents.Where(g => g.Enabled && g.Status.Equals("online", StringComparison.OrdinalIgnoreCase)).ToList();
            string Who(Func<AzdoAgent, bool> f) => live.Where(f).Select(g => $"{g.Name} ({g.Os})").DefaultIfEmpty("none").Aggregate((p, q) => p + ", " + q);
            bool? Any(Func<AzdoAgent, bool> f) => pool.Agents.Count == 0 || pool.Agents.All(g => g.Capabilities.Count == 0) ? null : live.Any(f);
            Add("both", "an agent is online", live.Count > 0, $"{live.Count} of {pool.Agents.Count} online and enabled", "Start an agent in this pool.");
            Add("plugins", "agent with the .NET SDK", Any(g => Has(g, "^dotnet")), Who(g => Has(g, "^dotnet")), "Install the .NET SDK on an agent (or add a UseDotNet step), or the capability is simply not advertised: add a user capability `dotnet`.");
            Add("product", "agent with docker", Any(HasDocker), Who(HasDocker), "The product pipeline starts the stack on the agent: it needs docker (with compose) or podman.");
            Add("product", "agent with bash", Any(g => Has(g, "^(bash|sh)$") || !g.Os.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)), Who(g => Has(g, "^(bash|sh)$") || !g.Os.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)), "The generated steps are bash. On a Windows agent that means Git for Windows on PATH.");
            Add("product", "agent with a browser for Selenium", Any(g => Has(g, "chrome|firefox")), Who(g => Has(g, "chrome|firefox")), "Install Chrome (or run the browser as a container next to the stack). Often simply not advertised as a capability.");
        }

        // credentials and targets
        var group = a.VariableGroups.FirstOrDefault(g => g.StartsWith(s["PIPELINE_CREDENTIALS_GROUP"] + " (", StringComparison.OrdinalIgnoreCase));
        Add("both", "variable group with the JFrog credentials", a.Project is null ? null : group is not null && group.Contains("JFROG_USER") && group.Contains("JFROG_TOKEN"),
            group ?? $"groups: {(a.VariableGroups.Count == 0 ? "none readable" : string.Join("; ", a.VariableGroups))}", $"Create the library variable group '{s["PIPELINE_CREDENTIALS_GROUP"]}' with JFROG_USER and JFROG_TOKEN (secret), and allow the pipelines to use it.");
        var jfrogExt = a.Extensions.Where(e => Regex.IsMatch(e, "jfrog|artifactory", RegexOptions.IgnoreCase)).ToList();
        var jfrogConn = a.ServiceConnections.Where(c => Regex.IsMatch(c.Name + " " + c.Detail, "jfrog|artifactory", RegexOptions.IgnoreCase)).ToList();
        checks.Add(new("both", "JFrog extension / service connection (optional)", jfrogExt.Count + jfrogConn.Count > 0 ? "ok" : "unknown",
            jfrogExt.Count + jfrogConn.Count > 0 ? string.Join("; ", jfrogExt.Concat(jfrogConn.Select(c => $"connection {c.Name} ({c.Detail})"))) : "none found; the generated YAML does not need one (it uses dotnet, docker and curl with the variable group)", ""));

        var j = x.Jfrog;
        Add("both", "Artifactory answers", j.Repos.Count > 0, j.Repos.Count > 0 ? $"{j.Url}: {j.Repos.Count} repositories" : string.Join("; ", j.Notes.Take(2)), "Check JFROG_URL, JFROG_USER and JFROG_TOKEN.");
        foreach (var (key, package, pipeline) in new[] { ("PIPELINE_NUGET_REPO_DEV", "nuget", "plugins"), ("PIPELINE_NUGET_REPO_PROD", "nuget", "plugins"), ("PIPELINE_DOCKER_REPO_DEV", "docker", "product"), ("PIPELINE_DOCKER_REPO_PROD", "docker", "product") })
        {
            var repo = j.Repos.FirstOrDefault(r => r.Key.Equals(s[key], StringComparison.OrdinalIgnoreCase));
            var ok = j.Repos.Count == 0 ? (bool?)null : repo is not null && repo.PackageType.Equals(package, StringComparison.OrdinalIgnoreCase);
            Add(pipeline, $"{package} repository ({(key.EndsWith("DEV") ? "dev" : "prod")})", ok,
                repo is not null ? $"{repo.Key}: {repo.Type} {repo.PackageType}{(repo.Type.Equals("local", StringComparison.OrdinalIgnoreCase) ? "" : " - pushing needs a LOCAL repository (or a virtual one with a default deployment repository)")}"
                    : $"{package} repositories: {string.Join(", ", j.Repos.Where(r => r.PackageType.Equals(package, StringComparison.OrdinalIgnoreCase)).Select(r => $"{r.Key} ({r.Type})"))}", $"Set {key}.");
        }

        var packages = s.List("PIPELINE_PACKAGES").ToList();
        Add("product", "plugin package ids", packages.Count > 0, packages.Count > 0 ? string.Join(", ", packages) : $"packages seen in Artifactory: {string.Join(", ", j.NugetVersions.Keys.Take(12))}", "Set PIPELINE_PACKAGES (they exist only after the plugins pipeline has run once).");
        Add("product", "services to bake", s.List("PIPELINE_BAKE_SERVICES").Any(), s["PIPELINE_BAKE_SERVICES"], "Set PIPELINE_BAKE_SERVICES (the map tab shows which services have bind mounts and plugins).");
        return checks;
    }

    // ---------------------------------------------------------------- how versions line up

    /// Every version seen anywhere, newest first, and where it was seen: the evidence for how releases are numbered.
    public static List<VersionRow> VersionTable(Exploration x, PipelineSettings s, VersionScheme scheme)
    {
        var prefix = s["PIPELINE_TAG_PREFIX"];
        HashSet<string> Tags(string key) => x.Azdo.Repos.FirstOrDefault(r => r.Name.Equals(s[key], StringComparison.OrdinalIgnoreCase))?.Tags
            .Select(t => t.StartsWith(prefix, StringComparison.Ordinal) ? t[prefix.Length..] : t).Select(scheme.Of).OfType<string>().ToHashSet() ?? [];
        var (pt, ct) = (Tags("PIPELINE_PLUGINS_REPO"), Tags("PIPELINE_CONFIG_REPO"));
        var packages = x.Jfrog.NugetVersions.Where(kv => !s.List("PIPELINE_PACKAGES").Any() || s.List("PIPELINE_PACKAGES").Any(p => kv.Key.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(kv => kv.Value).Select(v => scheme.Of(v) ?? v).ToHashSet();
        var images = x.Jfrog.DockerTags.SelectMany(kv => kv.Value).Select(scheme.Of).OfType<string>().ToHashSet();
        return pt.Concat(ct).Concat(packages).Concat(images).Distinct().OrderByDescending(v => v, Comparer<string>.Create(Versions.Compare))
            .Select(v => new VersionRow(v, pt.Contains(v), ct.Contains(v), packages.Contains(v), images.Contains(v))).ToList();
    }

    /// What the tags in the repositories look like, so the tag prefix can be set from evidence.
    public static string TagConvention(Exploration x)
    {
        var tags = x.Azdo.Repos.SelectMany(r => r.Tags).ToList();
        if (tags.Count == 0) return "no git tags found in any repository";
        var shapes = tags.GroupBy(t => Regex.Replace(Regex.Replace(t, @"\d+", "N"), @"N(\.N)+", "N.N.N")).OrderByDescending(g => g.Count()).Take(4);
        return string.Join("; ", shapes.Select(g => $"{g.Count()} like {g.Key} (e.g. {g.First()})"));
    }

    // ---------------------------------------------------------------- the pipelines themselves

    private const string Header = "# GENERATED by toolrunner (pipelines). Edit the settings and regenerate, or take this file over and edit it by hand.\n";

    private static string EnvSwitch(string name, string dev, string prod) =>
        $"- name: {name}\n  ${{{{ if eq(parameters.environment, 'prod') }}}}:\n    value: {prod}\n  ${{{{ if ne(parameters.environment, 'prod') }}}}:\n    value: {dev}\n";

    private static string Common(PipelineSettings s) =>
        "parameters:\n- name: environment\n  displayName: JFrog target (dev or prod repositories)\n  type: string\n  default: dev\n  values: [ dev, prod ]\n\n"
        + $"trigger:\n  branches:\n    include: [ {s["PIPELINE_MAIN_BRANCH"]}, release/* ]\n  tags:\n    include: [ '{s["PIPELINE_TAG_PREFIX"]}*' ]\n\n"
        + $"pool:\n  name: {s["PIPELINE_POOL"]}\n\n";

    /// A release is a build of the tag <prefix><version>; anything else is <last version>-ci.<build id>. Prod takes releases only.
    private static string VersionStep(PipelineSettings s, string workDir) =>
        $$$"""
        - bash: |
            set -euo pipefail
            prefix='{{{s["PIPELINE_TAG_PREFIX"]}}}'
            tag=`git describe --tags --abbrev=0 --match "${prefix}[0-9]*" 2>/dev/null || true`
            base="${tag#$prefix}"; base="${base:-0.1.0}"
            if [ "$(Build.SourceBranch)" = "refs/tags/$tag" ]; then version="$base"; else version="$base-ci.$(Build.BuildId)"; fi
            if [ "${{ parameters.environment }}" = "prod" ] && [ "$version" != "$base" ]; then echo "##vso[task.logissue type=error]prod is built from a version tag only (this is $version)"; exit 1; fi
            echo "version $version"
            echo "##vso[task.setvariable variable=version]$version"
            echo "##vso[build.updatebuildnumber]$version"
          displayName: version from the git tag
          workingDirectory: {{{workDir}}}

        """;

    public static string PluginsYaml(PipelineSettings s, bool hasSolution = true) =>
        Header + "# Pipeline 1: build and unit-test the plugins, pack them as NuGet, push to JFrog.\n\n" + Common(s)
        + $"variables:\n- group: {s["PIPELINE_CREDENTIALS_GROUP"]}\n- name: jfrogUrl\n  value: {s["JFROG_URL"].TrimEnd('/')}\n"
        + EnvSwitch("nugetRepo", s["PIPELINE_NUGET_REPO_DEV"], s["PIPELINE_NUGET_REPO_PROD"])
        + "\nsteps:\n- checkout: self\n  fetchDepth: 0          # the version comes from the tags\n\n"
        + VersionStep(s, "$(Build.SourcesDirectory)")
        + $$$"""
        - task: DotNetCoreCLI@2
          displayName: restore
          inputs:
            command: restore
            projects: '{{{(hasSolution ? "**/*.sln" : "**/*.csproj")}}}'

        - task: DotNetCoreCLI@2
          displayName: build
          inputs:
            command: build
            projects: '{{{(hasSolution ? "**/*.sln" : "**/*.csproj")}}}'
            arguments: -c Release --no-restore -p:Version=$(version)

        - task: DotNetCoreCLI@2
          displayName: unit tests
          inputs:
            command: test
            projects: '**/*Test*.csproj'
            arguments: -c Release --no-build
            publishTestResults: true

        - bash: dotnet pack -c Release --no-build -p:PackageVersion=$(version) -o "$(Build.ArtifactStagingDirectory)/packages"
          displayName: pack

        - bash: |
            set -euo pipefail
            for package in "$(Build.ArtifactStagingDirectory)"/packages/*.nupkg; do
              dotnet nuget push "$package" --source "$(jfrogUrl)/api/nuget/$(nugetRepo)" --api-key "$JFROG_USER:$JFROG_TOKEN"
            done
          displayName: push to JFrog ($(nugetRepo))
          env:
            JFROG_USER: $(JFROG_USER)
            JFROG_TOKEN: $(JFROG_TOKEN)

        - publish: $(Build.ArtifactStagingDirectory)/packages
          artifact: packages

        """.Replace("\r\n", "\n");

    public static string ProductYaml(PipelineSettings s)
    {
        var config = s["PIPELINE_CONFIG_REPO"];
        var tests = s["PIPELINE_TESTS_REPO"];
        var compose = $"-f \"{s["PIPELINE_COMPOSE_FILE"]}\" --env-file \"{s["PIPELINE_ENV_FILE"]}\"";
        var services = string.Join(" ", s.List("PIPELINE_BAKE_SERVICES"));
        var packages = string.Join(" ", s.List("PIPELINE_PACKAGES"));
        return (Header + "# Pipeline 2: fetch the plugins of the same version, start the stack, run the Selenium tests, then bake config and plugins\n# into final images (no bind mounts) and push them to JFrog.\n\n" + Common(s)
            + $"resources:\n  repositories:\n  - repository: tests\n    type: git\n    name: {s["AZDO_PROJECT"]}/{tests}\n    ref: refs/heads/{s["PIPELINE_MAIN_BRANCH"]}\n\n"
            + $"variables:\n- group: {s["PIPELINE_CREDENTIALS_GROUP"]}\n- name: jfrogUrl\n  value: {s["JFROG_URL"].TrimEnd('/')}\n- name: product\n  value: {s["PIPELINE_PRODUCT"]}\n- name: configDir\n  value: $(Build.SourcesDirectory)/{config}\n"
            + EnvSwitch("nugetRepo", s["PIPELINE_NUGET_REPO_DEV"], s["PIPELINE_NUGET_REPO_PROD"])
            + EnvSwitch("dockerRegistry", s["PIPELINE_DOCKER_REGISTRY_DEV"], s["PIPELINE_DOCKER_REGISTRY_PROD"])
            + "\nsteps:\n- checkout: self\n  fetchDepth: 0\n- checkout: tests\n\n"
            + VersionStep(s, "$(configDir)")
            + $$$"""
            - bash: |
                set -euo pipefail
                mkdir -p "{{{s["PIPELINE_PLUGINS_DIR"]}}}" _packages
                for id in {{{packages}}}; do
                  curl -fsSL -u "$JFROG_USER:$JFROG_TOKEN" -o "_packages/$id.nupkg" "$(jfrogUrl)/api/nuget/$(nugetRepo)/Download/$id/$(version)"
                  unzip -o -j "_packages/$id.nupkg" 'lib/*/*' -d "{{{s["PIPELINE_PLUGINS_DIR"]}}}"
                done
                ls -l "{{{s["PIPELINE_PLUGINS_DIR"]}}}"
              displayName: fetch the plugins $(version) from JFrog
              workingDirectory: $(configDir)
              env:
                JFROG_USER: $(JFROG_USER)
                JFROG_TOKEN: $(JFROG_TOKEN)

            - bash: echo "$JFROG_TOKEN" | docker login "$(dockerRegistry)" -u "$JFROG_USER" --password-stdin
              displayName: docker login
              env:
                JFROG_USER: $(JFROG_USER)
                JFROG_TOKEN: $(JFROG_TOKEN)

            - bash: |
                set -euo pipefail
                docker compose {{{compose}}} up -d
                for i in `seq 1 60`; do
                  if curl -fsS -o /dev/null "{{{s["PIPELINE_HEALTH_URL"]}}}"; then echo "portal answers"; exit 0; fi
                  sleep 5
                done
                echo "##vso[task.logissue type=error]the portal did not answer at {{{s["PIPELINE_HEALTH_URL"]}}} within 5 minutes"; exit 1
              displayName: start the stack and wait for the portal
              workingDirectory: $(configDir)

            - task: DotNetCoreCLI@2
              displayName: Selenium tests
              inputs:
                command: test
                projects: '$(Build.SourcesDirectory)/{{{tests}}}/{{{s["PIPELINE_TESTS_PROJECTS"]}}}'
                arguments: -c Release
                publishTestResults: true
              env:
                PORTAL_URL: {{{s["PIPELINE_HEALTH_URL"]}}}

            - bash: |
                set -euo pipefail
                for service in {{{services}}}; do
                  base=`docker inspect --format '{{.Config.Image}}' $(docker compose {{{compose}}} ps -q "$service")`
                  image="$(dockerRegistry)/$(product)-$service:$(version)"
                  docker build -f "pipeline/Dockerfile.$service" --build-arg BASE="$base" --label "org.opencontainers.image.version=$(version)" --label "org.opencontainers.image.revision=$(Build.SourceVersion)" -t "$image" .
                  docker push "$image"
                  echo "pushed $image (on $base)"
                done
              displayName: bake config and plugins into the final images and push
              workingDirectory: $(configDir)

            - bash: docker compose {{{compose}}} logs --no-color > "$(Build.ArtifactStagingDirectory)/stack.log" 2>&1 || true
              displayName: collect the stack's logs
              condition: always()
              workingDirectory: $(configDir)

            - publish: $(Build.ArtifactStagingDirectory)/stack.log
              artifact: stack-log
              condition: always()

            - bash: docker compose {{{compose}}} down --volumes
              displayName: stop the stack
              condition: always()
              workingDirectory: $(configDir)

            """).Replace("\r\n", "\n");
    }

    /// The final image of one service: its base image plus everything it used to get through bind mounts.
    /// Sources are paths in the config repository (the build context). Log folders are left out.
    public static (string Text, List<string> Notes) BakeDockerfile(string service, string repoRoot, IEnumerable<MountRef> mounts)
    {
        var sb = new StringBuilder(Header + $"# Final image of {service}: what was bind-mounted is copied in. Built by the product pipeline with --build-arg BASE=<the image the stack ran>.\nARG BASE\nFROM ${{BASE}}\n");
        var notes = new List<string>();
        var root = ComposeLayout.Collapse(repoRoot).TrimEnd('/');
        foreach (var m in mounts.Where(m => m.Service == service && m.Kind == "bind"))
        {
            var resolved = ComposeLayout.Collapse(m.Resolved ?? m.Source ?? "");
            if (Regex.IsMatch(m.Target, @"(^|/)logs?(/|$)", RegexOptions.IgnoreCase)) { notes.Add($"{m.Target}: left out (a log folder is written to, not shipped)"); continue; }
            if (!resolved.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{m.Target}: its source {resolved} is outside the config repository; not copied"); continue; }
            var rel = resolved[(root.Length + 1)..];
            var isFile = File.Exists(resolved) || (!Directory.Exists(resolved) && Path.HasExtension(resolved));
            sb.Append(isFile ? $"COPY {rel} {m.Target}\n" : $"COPY {rel}/ {m.Target.TrimEnd('/')}/\n");
        }
        if (!sb.ToString().Contains("\nCOPY ")) notes.Add($"{service} has no bind mount inside the config repository: the image would equal its base");
        return (sb.ToString(), notes);
    }

    /// The files to commit: (repository setting, path in that repository, content).
    public static List<(string Repo, string Path, string Content)> Files(PipelineSettings s, Stack? stack, bool pluginsHaveSolution = true)
    {
        var files = new List<(string, string, string)>
        {
            (s["PIPELINE_PLUGINS_REPO"], "azure-pipelines.yml", PluginsYaml(s, pluginsHaveSolution)),
            (s["PIPELINE_CONFIG_REPO"], "azure-pipelines.yml", ProductYaml(s))
        };
        foreach (var service in s.List("PIPELINE_BAKE_SERVICES"))
            files.Add((s["PIPELINE_CONFIG_REPO"], $"pipeline/Dockerfile.{service}",
                stack is null ? Header + $"# No scan of the config repository was available: list what {service} needs.\nARG BASE\nFROM ${{BASE}}\n# COPY <path in this repository> <path in the container>\n"
                              : BakeDockerfile(service, stack.Dir, stack.Mounts).Text));
        return files;
    }
}
