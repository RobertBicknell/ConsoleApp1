using System.Net;
using ToolRunner.Core;

namespace ToolRunner.Tests;

/// Exploring Azure DevOps and JFrog (against canned answers), judging readiness, and generating the two pipelines.
public class PipelineTests
{
    /// Answers by the first route whose text the request path contains; anything else is 404.
    private sealed class Server(params (string Contains, string Json)[] routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(r.RequestUri!.PathAndQuery);
            Requests.Add($"{r.Method} {url}");
            foreach (var (contains, json) in routes)
                if (url.Contains(contains)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }

    private static string Items(params string[] paths) => "{\"value\":[" + string.Join(",", paths.Select(p => $"{{\"path\":\"{p}\"}}")) + "]}";

    private static Server World() => new(
        // Azure DevOps Server 2020: api 7.x is refused, 6.0 answers
        ("_apis/projects?api-version=7", "not json: a sign-in page"),
        ("_apis/projects?api-version=6.0", """{"value":[{"name":"Acme"}]}"""),
        ("pools/7/agents", """{"value":[{"name":"build-linux-01","status":"online","enabled":true,"systemCapabilities":{"Agent.OS":"Linux","docker":"/usr/bin/docker","dotnet":"/usr/bin/dotnet","bash":"/bin/bash","PATH":"/usr/bin","google-chrome":"/usr/bin/google-chrome"}}]}"""),
        ("pools/9/agents", """{"value":[{"name":"win-01","status":"offline","enabled":true,"systemCapabilities":{"Agent.OS":"Windows_NT","MSBuild":"C:\\x"}}]}"""),
        ("_apis/distributedtask/pools", """{"value":[{"id":7,"name":"Linux-Docker","isHosted":false},{"id":9,"name":"Windows","isHosted":false}]}"""),
        ("installedextensions", """{"value":[{"publisherId":"JFrog","extensionId":"jfrog-artifactory-vsts-extension","version":"1.13.0"}]}"""),
        ("_apis/distributedtask/tasks", """{"value":[{"name":"DotNetCoreCLI","version":{"major":2}},{"name":"Docker","version":{"major":2}}]}"""),
        ("repositories/r1/refs?filter=tags", """{"value":[{"name":"refs/tags/v2.4.0"},{"name":"refs/tags/v2.4.1"}]}"""),
        ("repositories/r2/refs?filter=tags", """{"value":[{"name":"refs/tags/v2.4.1"}]}"""),
        ("refs?filter=heads", """{"value":[{"name":"refs/heads/main"},{"name":"refs/heads/release/2.4"}]}"""),
        ("refs?filter=tags", """{"value":[]}"""),
        ("repositories/r1/items?scopePath", Items("/Acme.Plugins.sln", "/src/Acme.Loyalty/Acme.Loyalty.csproj", "/tests/Acme.Loyalty.Tests/Acme.Loyalty.Tests.csproj", "/README.md")),
        ("repositories/r2/items?scopePath", Items("/docker-compose.yml", "/.env", "/product.json", "/config/webportal/appsettings.json")),
        ("repositories/r3/items?scopePath", Items("/Acme.UiTests/Acme.UiTests.csproj")),
        ("repositories/r3/items?path=/Acme.UiTests/Acme.UiTests.csproj", """<Project><ItemGroup><PackageReference Include="Selenium.WebDriver" Version="4.20.0" /></ItemGroup></Project>"""),
        ("items?path=", "<Project />"),
        ("_apis/git/repositories", """{"value":[{"id":"r1","name":"acme-plugins","defaultBranch":"refs/heads/main"},{"id":"r2","name":"acme-config","defaultBranch":"refs/heads/main"},{"id":"r3","name":"acme-uitests","defaultBranch":"refs/heads/main"}]}"""),
        ("serviceendpoint/endpoints", """{"value":[{"name":"jfrog-dev","type":"artifactoryService","url":"https://art.corp/artifactory"}]}"""),
        ("variablegroups", """{"value":[{"name":"jfrog-credentials","variables":{"JFROG_USER":{},"JFROG_TOKEN":{}}}]}"""),
        ("build/definitions", """{"value":[{"name":"legacy","process":{"yamlFilename":"ci.yml"},"repository":{"name":"acme-plugins"},"queue":{"name":"Windows"}}]}"""),
        ("build/builds", """{"value":[{"definition":{"name":"legacy"},"buildNumber":"2.4.1.118","sourceBranch":"refs/heads/main","result":"succeeded"}]}"""),
        // Artifactory
        ("/artifactory/api/repositories", """[{"key":"nuget-dev-local","type":"LOCAL","packageType":"nuget"},{"key":"nuget-prod-local","type":"LOCAL","packageType":"nuget"},{"key":"docker-dev-local","type":"LOCAL","packageType":"docker"},{"key":"docker-prod-local","type":"LOCAL","packageType":"docker"},{"key":"docker-remote","type":"REMOTE","packageType":"docker"}]"""),
        ("api/storage/nuget-dev-local", """{"children":[{"uri":"/Acme.Loyalty.2.4.0.nupkg","folder":false},{"uri":"/Acme.Loyalty.2.4.1.nupkg","folder":false}]}"""),
        ("api/storage/nuget-prod-local", """{"children":[{"uri":"/Acme.Loyalty.2.4.0.nupkg","folder":false}]}"""),
        ("docker-dev-local/v2/_catalog", """{"repositories":["acme-webportal"]}"""),
        ("docker-dev-local/v2/acme-webportal/tags/list", """{"tags":["2.4.0","2.4.0-ci.117"]}"""),
        ("v2/_catalog", """{"repositories":[]}"""));

    private static PipelineSettings Settings(params (string, string)[] set)
    {
        var values = new Dictionary<string, string> { ["AZDO_URL"] = "https://azd.corp/tfs/Main", ["AZDO_PAT"] = "pat", ["JFROG_URL"] = "https://art.corp/artifactory", ["JFROG_USER"] = "me", ["JFROG_TOKEN"] = "t" };
        foreach (var (k, v) in set) values[k] = v;
        return PipelineSettings.From(k => values.GetValueOrDefault(k));
    }

    private static PipelineSettings With(PipelineSettings s, Dictionary<string, string> more, params (string, string)[] set) =>
        new(s.All.Select(a => set.FirstOrDefault(x => x.Item1 == a.Var) is { Item1: not null } x ? a with { Value = x.Item2 } : more.TryGetValue(a.Var, out var v) ? a with { Value = v } : a).ToList());

    [Fact]
    public async Task Exploring_finds_the_api_version_the_three_repositories_the_agents_and_the_jfrog_repositories()
    {
        var server = World();
        var x = await Pipelines.ExploreAsync(Settings(), CancellationToken.None, handler: server);

        Assert.Equal("6.0", x.Azdo.ApiVersion);
        Assert.Equal("Acme", x.Azdo.Project);                                                   // the only project is taken
        Assert.Equal([("acme-config", "config"), ("acme-plugins", "plugins"), ("acme-uitests", "tests")], x.Azdo.Repos.Select(r => (r.Name, r.Kind)).Order());
        Assert.Equal(["v2.4.0", "v2.4.1"], x.Azdo.Repos.Single(r => r.Name == "acme-plugins").Tags);
        var agent = x.Azdo.Pools.Single(p => p.Name == "Linux-Docker").Agents.Single();
        Assert.Equal("Linux", agent.Os);
        Assert.Contains("docker", agent.Capabilities.Keys);
        Assert.DoesNotContain("PATH", agent.Capabilities.Keys);                                  // only the capabilities that matter are kept
        Assert.Contains(x.Azdo.Extensions, e => e.StartsWith("JFrog."));
        Assert.Equal(["2.4.0", "2.4.1"], x.Jfrog.NugetVersions["nuget-dev-local/Acme.Loyalty"]);  // the suggested repositories were looked into at once
        Assert.Equal(["2.4.0", "2.4.0-ci.117"], x.Jfrog.DockerTags["docker-dev-local/acme-webportal"]);
        Assert.All(server.Requests, r => Assert.StartsWith("GET ", r));                          // exploring never writes
    }

    [Fact]
    public async Task What_was_found_fills_empty_settings_and_the_assessment_says_what_is_ready_and_what_is_not()
    {
        var s = Settings();
        var x = await Pipelines.ExploreAsync(s, CancellationToken.None, handler: World());

        var suggested = Pipelines.Suggest(x, s);
        Assert.Equal("acme-plugins", suggested["PIPELINE_PLUGINS_REPO"]);
        Assert.Equal("acme-uitests", suggested["PIPELINE_TESTS_REPO"]);
        Assert.Equal("Linux-Docker", suggested["PIPELINE_POOL"]);                                 // the pool whose agents have docker
        Assert.Equal(("nuget-dev-local", "nuget-prod-local"), (suggested["PIPELINE_NUGET_REPO_DEV"], suggested["PIPELINE_NUGET_REPO_PROD"]));
        Assert.Equal("art.corp/docker-prod-local", suggested["PIPELINE_DOCKER_REGISTRY_PROD"]);

        var before = Pipelines.Assess(x, s);
        Assert.Contains(before, c => c.Name == "plugins repository" && c.Status == "missing" && c.Evidence.Contains("looks like: acme-plugins"));

        var ready = With(s, suggested, ("PIPELINE_PACKAGES", "Acme.Loyalty"), ("PIPELINE_BAKE_SERVICES", "webportal"));
        var after = Pipelines.Assess(x, ready);
        Assert.DoesNotContain(after, c => c.Status == "missing");
        Assert.Contains(after, c => c.Name == "agent with docker" && c.Evidence.Contains("build-linux-01 (Linux)"));
        Assert.Contains(after, c => c.Name == "unit test projects" && c.Status == "ok");

        var windows = Pipelines.Assess(x, With(ready, [], ("PIPELINE_POOL", "Windows")));
        Assert.Contains(windows, c => c.Name == "an agent is online" && c.Status == "missing");
        Assert.Contains(windows, c => c.Name == "agent with docker" && c.Status == "missing");
    }

    [Fact]
    public async Task Versions_are_lined_up_across_tags_packages_and_images()
    {
        var s = Settings();
        var x = await Pipelines.ExploreAsync(s, CancellationToken.None, handler: World());
        var rows = Pipelines.VersionTable(x, With(s, Pipelines.Suggest(x, s)), VersionScheme.From(_ => null));

        Assert.Equal(new VersionRow("2.4.1", PluginsTag: true, ConfigTag: true, Package: true, Image: false), rows[0]);   // tagged and packed, no image yet
        Assert.Equal(new VersionRow("2.4.0", true, false, true, true), rows[1]);
        Assert.Contains("like vN.N.N", Pipelines.TagConvention(x));
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_not_thrown()
    {
        var x = await Pipelines.ExploreAsync(Settings(), CancellationToken.None, handler: new Server());
        Assert.Null(x.Azdo.ApiVersion);
        Assert.Contains(x.Azdo.Notes, n => n.Contains("COLLECTION"));
        Assert.Contains(Pipelines.Assess(x, Settings()), c => c.Name == "Azure DevOps answers" && c.Status == "missing");
    }

    private static PipelineSettings Full() => Settings(
        ("AZDO_PROJECT", "Acme"), ("PIPELINE_POOL", "Linux-Docker"), ("PIPELINE_PLUGINS_REPO", "acme-plugins"), ("PIPELINE_CONFIG_REPO", "acme-config"), ("PIPELINE_TESTS_REPO", "acme-uitests"),
        ("PIPELINE_NUGET_REPO_DEV", "nuget-dev-local"), ("PIPELINE_NUGET_REPO_PROD", "nuget-prod-local"),
        ("PIPELINE_DOCKER_REGISTRY_DEV", "art.corp/docker-dev-local"), ("PIPELINE_DOCKER_REGISTRY_PROD", "art.corp/docker-prod-local"),
        ("PIPELINE_PACKAGES", "Acme.Loyalty;Acme.Billing"), ("PIPELINE_BAKE_SERVICES", "webportal"), ("PIPELINE_PRODUCT", "acme"));

    [Fact]
    public void The_plugins_pipeline_builds_tests_packs_and_pushes_to_the_repository_of_the_chosen_environment()
    {
        var yaml = Pipelines.PluginsYaml(Full());

        Assert.Contains("values: [ dev, prod ]", yaml);
        Assert.Contains("- name: nugetRepo\n  ${{ if eq(parameters.environment, 'prod') }}:\n    value: nuget-prod-local\n  ${{ if ne(parameters.environment, 'prod') }}:\n    value: nuget-dev-local\n", yaml);
        Assert.Contains("pool:\n  name: Linux-Docker", yaml);
        Assert.Contains("- group: jfrog-credentials", yaml);
        Assert.Contains("--match \"${prefix}[0-9]*\"", yaml);
        Assert.Contains("version=\"$base-ci.$(Build.BuildId)\"", yaml);
        Assert.Contains("if [ \"${{ parameters.environment }}\" = \"prod\" ]", yaml);              // prod only from a version tag
        Assert.True(yaml.IndexOf("command: build") < yaml.IndexOf("command: test") && yaml.IndexOf("command: test") < yaml.IndexOf("dotnet pack"));
        Assert.Contains("dotnet nuget push \"$package\" --source \"$(jfrogUrl)/api/nuget/$(nugetRepo)\" --api-key \"$JFROG_USER:$JFROG_TOKEN\"", yaml);
        Assert.DoesNotContain("{{{", yaml);
        Assert.DoesNotContain("\t", yaml);
    }

    [Fact]
    public void The_product_pipeline_fetches_the_plugins_starts_the_stack_tests_bakes_pushes_and_always_cleans_up()
    {
        var yaml = Pipelines.ProductYaml(Full());

        Assert.Contains("name: Acme/acme-uitests", yaml);
        Assert.Contains("for id in Acme.Loyalty Acme.Billing; do", yaml);
        Assert.Contains("\"$(jfrogUrl)/api/nuget/$(nugetRepo)/Download/$id/$(version)\"", yaml);
        Assert.Contains("value: art.corp/docker-prod-local", yaml);
        Assert.Contains("docker compose -f \"docker-compose.yml\" --env-file \".env\" up -d", yaml);
        Assert.Contains("docker inspect --format '{{.Config.Image}}'", yaml);
        Assert.Contains("image=\"$(dockerRegistry)/$(product)-$service:$(version)\"", yaml);
        Assert.Contains("docker build -f \"pipeline/Dockerfile.$service\" --build-arg BASE=\"$base\"", yaml);
        string[] order = ["fetch the plugins", "docker login", "start the stack", "Selenium tests", "bake config and plugins", "collect the stack's logs", "stop the stack"];
        Assert.Equal(order, order.OrderBy(o => yaml.IndexOf("displayName: " + o, StringComparison.Ordinal)));
        Assert.True(yaml.IndexOf("stop the stack") < yaml.LastIndexOf("condition: always()"));       // the stack goes down whatever happened
        Assert.DoesNotContain("condition", yaml[yaml.IndexOf("bake config")..yaml.IndexOf("collect the stack")]);   // images only after green tests
    }

    [Fact]
    public void A_baked_image_copies_what_the_service_bind_mounted_and_nothing_else()
    {
        MountRef Mount(string service, string kind, string resolved, string target) => new("c.yml", 1, service, kind, resolved, resolved, target, false, "", true);
        var (text, notes) = Pipelines.BakeDockerfile("webportal", "/repo/config", [
            Mount("webportal", "bind", "/repo/config/config/webportal", "/app/config"),
            Mount("webportal", "bind", "/repo/config/plugins", "/app/plugins/"),
            Mount("webportal", "bind", "/repo/config/config/webportal/appsettings.json", "/app/appsettings.json"),
            Mount("webportal", "bind", "/repo/config/logs/webportal", "/app/logs"),
            Mount("webportal", "bind", "/etc/ssl/corp", "/certs"),
            Mount("webportal", "volume", "data", "/data"),
            Mount("db", "bind", "/repo/config/db", "/init")]);

        Assert.Contains("ARG BASE\nFROM ${BASE}\n", text);
        Assert.Contains("COPY config/webportal/ /app/config/\n", text);
        Assert.Contains("COPY plugins/ /app/plugins/\n", text);
        Assert.Contains("COPY config/webportal/appsettings.json /app/appsettings.json\n", text);
        Assert.DoesNotContain("/app/logs", text);
        Assert.DoesNotContain("/init", text);
        Assert.Contains(notes, n => n.Contains("/certs") && n.Contains("outside the config repository"));
    }

    [Fact]
    public async Task Registering_a_pipeline_posts_the_yaml_path_and_the_repository()
    {
        var server = new Server(("_apis/pipelines", """{"id":12}"""));
        var (ok, message) = await AzdoExplorer.CreatePipelineAsync("https://azd.corp/tfs/Main", "Acme", "pat", "6.0", "acme-plugins", "r1", "acme-plugins", "/azure-pipelines.yml", CancellationToken.None, server);
        Assert.True(ok, message);
        Assert.Equal("POST /tfs/Main/Acme/_apis/pipelines?api-version=6.0-preview.1", server.Requests.Single());
    }
}
