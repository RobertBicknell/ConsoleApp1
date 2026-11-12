using System.Net;
using System.Text.RegularExpressions;
using ToolRunner.Core;

namespace ToolRunner.Tests;

public class DockerfileTests
{
    public const string VsStyle = """
        ARG REGISTRY=registry.corp/docker
        FROM ${REGISTRY}/hardened/dotnet-aspnet:8.0-alpine AS base
        USER app
        WORKDIR /app
        EXPOSE 8080

        FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
        ARG BUILD_CONFIGURATION=Release
        WORKDIR /src
        COPY ["Acme.Gateway/Acme.Gateway.csproj", "Acme.Gateway/"]
        RUN dotnet restore "Acme.Gateway/Acme.Gateway.csproj"
        COPY . .
        RUN dotnet publish "Acme.Gateway/Acme.Gateway.csproj" -c Release -o /app/publish

        FROM base AS final
        WORKDIR /app
        COPY --from=build /app/publish .
        USER $APP_UID
        ENTRYPOINT ["dotnet", "Acme.Gateway.dll"]
        """;

    [Fact]
    public void The_runtime_chain_leads_from_the_final_stage_back_to_the_external_image()
    {
        var chain = Dockerfiles.RuntimeChain(Dockerfiles.Stages(VsStyle));
        Assert.Equal(["final", "base"], chain.Select(s => s.Alias));
        Assert.Equal("registry.corp/docker/hardened/dotnet-aspnet:8.0-alpine", chain[^1].Image);    // ARG default expanded
    }

    [Fact]
    public void The_debug_variant_swaps_only_the_runtime_base_and_adds_the_debugging_pieces()
    {
        var (text, changes) = Dockerfiles.DebugVariant(VsStyle, new DebugDockerfileOptions("registry.corp/docker/dotnet/aspnet:8.0-alpine", VsdbgUrl: "https://aka.ms/getvsdbgsh"));
        var lines = Dockerfiles.Lines(text);

        Assert.Contains("FROM registry.corp/docker/dotnet/aspnet:8.0-alpine AS base", lines);
        Assert.Contains("FROM ${REGISTRY}/dotnet/sdk:8.0 AS build", lines);                         // the build stage is untouched
        Assert.Contains("USER root", lines);
        Assert.Contains("# (debug image) USER app", lines);
        Assert.Contains("# (debug image) USER $APP_UID", lines);
        Assert.Contains(lines, l => l.Contains("-c Debug -o /app/publish"));
        Assert.Contains("ARG BUILD_CONFIGURATION=Debug", lines);
        Assert.Contains(lines, l => l.StartsWith("RUN curl -sSL https://aka.ms/getvsdbgsh | sh /dev/stdin -v latest -l /vsdbg"));
        Assert.Contains("ENV DOTNET_EnableDiagnostics=1", lines);
        Assert.True(Array.IndexOf(lines, "USER root") < Array.FindIndex(lines, l => l.StartsWith("FROM ${REGISTRY}/dotnet/sdk")));   // in the base stage
        Assert.Contains(changes, c => c.Contains("base image registry.corp/docker/hardened/dotnet-aspnet:8.0-alpine -> "));
    }

    [Fact]
    public void By_default_vsdbg_is_not_in_the_image_at_all()
    {
        var (text, _) = Dockerfiles.DebugVariant(VsStyle, new DebugDockerfileOptions("x/aspnet:8.0"));
        Assert.DoesNotContain("/vsdbg", text);
        Assert.DoesNotContain("getvsdbgsh", text);
    }

    [Fact]
    public void Vsdbg_can_come_from_the_build_context_instead_of_the_network()
    {
        var (text, _) = Dockerfiles.DebugVariant(VsStyle, new DebugDockerfileOptions("x/aspnet:8.0", VsdbgContextDir: ".toolrunner-vsdbg"));
        Assert.Contains("COPY .toolrunner-vsdbg/ /vsdbg/", text);
        Assert.DoesNotContain("getvsdbgsh", text);
    }

    [Fact]
    public void Facts_read_from_the_dockerfile()
    {
        Assert.Equal("Acme.Gateway", Dockerfiles.EntryAssembly(VsStyle));
        Assert.Equal(["Acme.Gateway/Acme.Gateway.csproj"], Dockerfiles.CopiedProjects(VsStyle));
        Assert.Equal("/src", Dockerfiles.SourceRoot(VsStyle));
    }
}

public class DebugImageChoiceTests
{
    [Fact]
    public void Candidates_are_the_same_kind_and_version_not_hardened_and_the_same_distro_family_first()
    {
        var ranked = DebugImages.Rank("registry.corp/docker/hardened/dotnet-aspnet:8.0-alpine",
        [
            "registry.corp/docker/dotnet/aspnet:8.0", "registry.corp/docker/dotnet/aspnet:8.0-alpine", "registry.corp/docker/dotnet/aspnet:8.0-alpine3.20",
            "registry.corp/docker/dotnet/aspnet:9.0-alpine", "registry.corp/docker/dotnet/sdk:8.0-alpine", "registry.corp/docker/dotnet/runtime:8.0-alpine",
            "registry.corp/docker/dotnet/aspnet:8.0-noble-chiseled", "registry.corp/docker/hardened/dotnet-aspnet:8.0-alpine", "myorg/gateway:dev"
        ]).Select(c => c.Image).ToList();

        Assert.Equal(["registry.corp/docker/dotnet/aspnet:8.0-alpine", "registry.corp/docker/dotnet/aspnet:8.0-alpine3.20", "registry.corp/docker/dotnet/aspnet:8.0"], ranked);
    }

    [Fact]
    public void A_project_is_matched_to_its_service_by_name()
    {
        ServiceInfo S(string name, string image) => new(name, image, null, [], false, "c.yml", 1);
        var services = new[] { S("gateway", "myorg/gateway:dev"), S("orders-api", "registry.corp/acme/orders-api:1.4"), S("db", "postgres:16") };

        Assert.Equal("orders-api", DebugImages.MatchService(services, ["Acme.Orders.Api"])[0].Service.Name);
        Assert.Equal("gateway", DebugImages.MatchService(services, ["Acme.Gateway", null])[0].Service.Name);
        Assert.Equal(0, DebugImages.MatchService(services, ["Acme.Billing"])[0].Score);
    }

    [Fact]
    public void The_override_file_holds_one_block_per_service_and_round_trips()
    {
        var blocks = DebugImages.ParseOverride(null);
        blocks["gateway"] = DebugImages.OverrideBlock("myorg/gateway:dev-debug", true, false);
        blocks["orders-api"] = DebugImages.OverrideBlock("x/orders-api:1-debug", false, true);
        var text = DebugImages.RenderOverride(blocks);

        Assert.Contains("  gateway:\n    image: myorg/gateway:dev-debug\n    pull_policy: never\n    user: root\n", text);
        Assert.Contains("    read_only: false", text);
        Assert.Equal(text, DebugImages.RenderOverride(DebugImages.ParseOverride(text)));
    }

    private sealed class Stub(Func<string, string?> answer) : HttpMessageHandler
    {
        public List<string> Auth { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Auth.Add(r.Headers.Authorization?.ToString() ?? "");
            var body = answer(r.RequestUri!.AbsolutePath);
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task The_registry_is_read_through_the_v2_catalog_and_tag_lists()
    {
        var stub = new Stub(p => p switch
        {
            "/artifactory/api/docker/docker-virtual/v2/_catalog" => """{ "repositories": ["dotnet/aspnet", "dotnet/sdk", "acme/orders-api", "dotnet/broken"] }""",
            "/artifactory/api/docker/docker-virtual/v2/dotnet/aspnet/tags/list" => """{ "name": "dotnet/aspnet", "tags": ["8.0", "8.0-alpine"] }""",
            "/artifactory/api/docker/docker-virtual/v2/dotnet/sdk/tags/list" => """{ "tags": ["8.0"] }""",
            _ => null
        });

        var (images, notes) = await RegistryClient.ListAsync("https://art.corp/artifactory/api/docker/docker-virtual/", "art.corp/docker-virtual", "me", "secret",
            new Regex("dotnet"), 25, CancellationToken.None, stub);

        Assert.Equal(["art.corp/docker-virtual/dotnet/aspnet:8.0", "art.corp/docker-virtual/dotnet/aspnet:8.0-alpine", "art.corp/docker-virtual/dotnet/sdk:8.0"], images);
        Assert.Contains(notes, n => n.Contains("dotnet/broken") && n.Contains("404"));
        Assert.All(stub.Auth, a => Assert.StartsWith("Basic ", a));
    }
}

/// The whole flow against the fake docker: Dockerfile.debug, build, override file, recreate the one service; and back.
[Collection("sandbox")]
public class DebugImageWorkflowTests
{
    private static string Project(Sandbox sb)
    {
        var dir = Path.Combine(sb.Root, "src", "Acme.Gateway");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Dockerfile"), DockerfileTests.VsStyle.Replace("\r\n", "\n"));
File.WriteAllText(Path.Combine(dir, "Acme.Gateway.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        // what Visual Studio leaves on a developer machine: the glibc build, and the musl build in a subfolder
        var vsdbg = Path.Combine(sb.Root, "vsdbg");
        Directory.CreateDirectory(Path.Combine(vsdbg, "linux-musl-x64"));
        File.WriteAllText(Path.Combine(vsdbg, "vsdbg"), "glibc"); File.WriteAllText(Path.Combine(vsdbg, "linux-musl-x64", "vsdbg"), "musl");
        File.WriteAllText(Path.Combine(sb.ConfigDir, "globals.user.json"),
            $$"""{ "APP_ROOT": "{{sb.App.Replace('\\', '/')}}", "VSDBG_DIR": "{{vsdbg.Replace('\\', '/')}}" }""");
        return dir.Replace('\\', '/');
    }

    private static async Task Pulled(Sandbox sb, params string[] images)     // the fake registers an image when it is "built"
    {
        foreach (var i in images)
            Assert.True((await sb.RunStep("docker", new() { ["verb"] = "build", ["tag"] = i, ["context"] = "." })).Ok);
    }

    [Fact]
    public async Task Debug_image_builds_beside_the_original_and_only_the_override_file_points_at_it()
    {
        using var sb = new Sandbox();
        var project = Project(sb);
        await Pulled(sb, "registry.corp/docker/dotnet/aspnet:8.0-alpine", "registry.corp/docker/dotnet/aspnet:8.0", "registry.corp/docker/dotnet/sdk:8.0");
        var composeBefore = File.ReadAllText(Path.Combine(sb.App, "docker-compose.yml"));
        var dockerfileBefore = File.ReadAllText(Path.Combine(project, "Dockerfile"));

        var r = await sb.Run("debug-image", project);

        Assert.True(r.Ok, r.Text);
        Assert.Equal("gateway", r.State.Vars["DEBUG_SERVICE"]);                                   // matched from Acme.Gateway
        Assert.Equal("registry.corp/docker/dotnet/aspnet:8.0-alpine", r.State.Vars["DEBUG_BASE_IMAGE"]);
        Assert.Equal(composeBefore, File.ReadAllText(Path.Combine(sb.App, "docker-compose.yml")));
        Assert.Equal(dockerfileBefore, File.ReadAllText(Path.Combine(project, "Dockerfile")));
        Assert.Contains("FROM registry.corp/docker/dotnet/aspnet:8.0-alpine AS base", File.ReadAllText(Path.Combine(project, "Dockerfile.debug")));

        var calls = sb.DockerCalls.Split('\n');
        var build = calls.Last(l => l.Contains(" build "));
        Assert.Contains("-t myorg/gateway:dev-debug", build);
        Assert.Contains("Acme.Gateway/Dockerfile.debug", build);
        Assert.EndsWith("/src", build.TrimEnd());                                                 // the context is where Acme.Gateway/Acme.Gateway.csproj resolves
        var up = calls.Last(l => l.Contains(" up "));
        Assert.Contains("docker-compose.yml -f", up);
        Assert.Contains("docker-compose.debug-image.yml", up);
        Assert.EndsWith("--no-deps gateway", up.TrimEnd());                                       // one service recreated, not the stack
        Assert.True(Array.IndexOf(calls, build) < Array.IndexOf(calls, up));

        Assert.Contains("\"Image\": \"myorg/gateway:dev-debug\"", File.ReadAllText(Path.Combine(sb.Root, "fakedocker-state.json")));
        var launch = File.ReadAllText(Path.Combine(sb.App, ".toolrunner", "attach-gateway.launch.json"));
Assert.Contains("exec -i myapp-gateway-1 /remote_debugger/vsdbg --interpreter=vscode", launch);
        var over = File.ReadAllText(Path.Combine(sb.App, ".toolrunner", "docker-compose.debug-image.yml"));
        Assert.Contains("/vsdbg/linux-musl-x64:/remote_debugger:ro", over);                       // alpine base: the musl debugger is mounted
        Assert.DoesNotContain("/vsdbg", File.ReadAllText(Path.Combine(project, "Dockerfile.debug")));
        Assert.Contains("\"/src\"", launch);
    }

    [Fact]
    public async Task Restore_image_removes_the_override_and_recreates_the_service_from_the_original()
    {
        using var sb = new Sandbox();
        var project = Project(sb);
        await Pulled(sb, "registry.corp/docker/dotnet/aspnet:8.0-alpine");
        Assert.True((await sb.Run("debug-image", project)).Ok);

        var r = await sb.Run("restore-image", "gateway");

        Assert.True(r.Ok, r.Text);
        Assert.False(File.Exists(Path.Combine(sb.App, ".toolrunner", "docker-compose.debug-image.yml")));
        var up = sb.DockerCalls.Split('\n').Last(l => l.Contains(" up "));
        Assert.DoesNotContain("debug-image", up);
        Assert.Contains("\"Image\": \"myorg/gateway:dev\"", File.ReadAllText(Path.Combine(sb.Root, "fakedocker-state.json")));
        Assert.True(File.Exists(Path.Combine(project, "Dockerfile.debug")));                      // kept: rebuilding later needs no new plan
    }

    [Fact]
    public async Task Without_any_candidate_the_plan_stops_and_asks_for_a_base_image()
    {
        using var sb = new Sandbox();
        var r = await sb.Run("debug-image-plan", Project(sb));
        Assert.False(r.Ok);
        Assert.Contains("give 'baseImage'", r.Text);
    }
}
