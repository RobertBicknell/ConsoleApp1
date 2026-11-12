using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tests;

public class VersionSchemeTests
{
    private static readonly VersionScheme Scheme = VersionScheme.From(_ => null);

    private const string Compose = """
        services:
          orders-api:
            image: ${REGISTRY}/acme/orders-api:${PRODUCT_VERSION}
          gateway:
            image: "${REGISTRY}/acme/gateway:${PRODUCT_VERSION}-alpine"
          billing:
            image: ${REGISTRY}/acme/billing:${BILLING_VERSION:-2.3.0}
          db:
            image: postgres:16.2
          cache:
            image: redis:latest
        """;

    private static StackVersions Read(string version = "2.4.1")
    {
        var env = new Dictionary<string, string> { ["REGISTRY"] = "art.corp:8443/docker", ["PRODUCT_VERSION"] = version };
        return Versions.Summarise(Versions.Read("core", ComposeLayout.Services("c.yml", Compose, env), new Dictionary<string, string> { ["c.yml"] = Compose }, Scheme));
    }

    [Fact]
    public void The_stack_version_is_what_most_variable_tagged_images_carry_and_the_rest_are_flagged()
    {
        var v = Read();
        Assert.Equal("2.4.1", v.Detected);
        Assert.Equal("PRODUCT_VERSION", v.Variable);
        Assert.Equal(["orders-api", "gateway", "billing"], v.Product.Select(i => i.Service));       // db and cache are third-party
        Assert.Equal(["billing"], v.Odd.Select(i => i.Service));                                    // 2.3.0
        Assert.Equal("2.4.1", v.Images.Single(i => i.Service == "gateway").Version);                // suffix ignored
        Assert.Null(v.Images.Single(i => i.Service == "db").Variable);                              // the registry port is not a tag
    }

    [Theory]
    [InlineData("2.4.1", "2.4.1")]
    [InlineData("2.4.1-alpine3.20", "2.4.1")]
    [InlineData("alpine3.20-2.4.1", "2.4.1")]          // the base image comes first: still not the version
    [InlineData("2.4.1-alpine-3.20.3", "2.4.1")]
    [InlineData("net8.0-2.4.1-bookworm12", "2.4.1")]
    [InlineData("alpine3.20", null)]                   // only a base image: no application version
    [InlineData("latest", "latest")]
    public void The_version_is_the_applications_never_the_base_images(string tag, string? expected) => Assert.Equal(expected, Scheme.Of(tag));

    [Fact]
    public void Latest_is_a_version()
    {
        Assert.Equal("latest", Read("latest").Detected);
        Assert.Equal(["origin/master", "origin/main", "master", "main"], Scheme.RefsFor("latest"));
    }

    [Fact]
    public void A_version_maps_to_git_refs_and_back_through_the_templates()
    {
        Assert.Equal(["v2.4.1", "2.4.1", "release/2.4.1", "origin/release/2.4.1"], Scheme.RefsFor("2.4.1"));
        Assert.Equal("2.4", Scheme.VersionOfRef("origin/release/2.4"));
        Assert.Equal("2.4.1", Scheme.VersionOfRef("v2.4.1"));
        Assert.Null(Scheme.VersionOfRef("feature/2.4.1-experiment"));

        var custom = VersionScheme.From(k => k == "VERSION_GIT_REFS" ? "rel-{version}" : k == "VERSION_PATTERN" ? @"\d{4}\.\d+" : null);
        Assert.Equal(["rel-2026.10"], custom.RefsFor("2026.10"));
        Assert.Equal("2026.10", custom.Of("2026.10-hotfix3"));
    }

    [Fact]
    public void Registry_tags_become_versions_highest_first_and_the_registry_path_drops_the_prefix()
    {
        Assert.Equal(["latest", "2.10.0", "2.4.1", "2.4.0"], Versions.FromTags(["2.4.0", "2.4.1", "2.4.1-alpine", "2.10.0", "latest", "sha-abc"], Scheme));
        Assert.Equal("acme/orders-api", Versions.RegistryRepo("art.corp/docker-virtual/acme/orders-api:2.4.1", "art.corp/docker-virtual"));
        Assert.Equal("acme/orders-api", Versions.RegistryRepo("art.corp:8443/acme/orders-api:2.4.1", null));
    }

    [Fact]
    public void A_dhi_image_is_replaced_by_its_own_dev_variant_first()
    {
        var ranked = DebugImages.Rank("dhi.io/aspnetcore:8-alpine3.22",
            ["dhi.io/aspnetcore:8-alpine3.22-dev", "dhi.io/aspnetcore:8-alpine3.22", "dhi.io/aspnetcore:9-alpine3.22-dev", "art.corp/dotnet/aspnet:8.0-alpine"]);
        Assert.Equal("dhi.io/aspnetcore:8-alpine3.22-dev", ranked[0].Image);
        Assert.DoesNotContain(ranked, r => r.Image.Contains(":9-"));
    }
}

/// Git refs as versions, and putting a source repo on the stack's version.
[Collection("sandbox")]
public class VersionWorkflowTests
{
    private static string Tagged(Sandbox sb)       // pluginhost: v1.0.0, then v1.1.0 (HEAD)
    {
        Sandbox.Git(sb.Plugin, "tag", "v1.0.0");
        File.WriteAllText(Path.Combine(sb.Plugin, "main.py"), "print('1.1')\n");
        Sandbox.Commit(sb.Plugin, "1.1");
        Sandbox.Git(sb.Plugin, "tag", "v1.1.0");
        File.WriteAllText(Path.Combine(sb.App, "docker-compose.yml"), "services:\n  pluginhost:\n    image: myorg/pluginhost:${V}\n  gateway:\n    image: myorg/gateway:${V}\n");
        File.AppendAllText(sb.Env, "V=1.0.0\n");
        return sb.Plugin.Replace('\\', '/');
    }

    [Fact]
    public async Task Versions_are_listed_from_git_and_the_stack_version_is_detected()
    {
        using var sb = new Sandbox();
        var repo = Tagged(sb);

        var git = await Versions.FromGitAsync(new LocalHost(), repo, VersionScheme.From(_ => null), CancellationToken.None);
        Assert.Equal(["latest", "1.1.0", "1.0.0"], git.Select(g => g.Version).Order(Comparer<string>.Create((a, b) => Versions.Compare(b, a))));

        var r = await sb.Run("versions");
        Assert.True(r.Ok, r.Text);
        Assert.Equal("1.0.0", r.State.Vars["VERSION_DETECTED"]);
        Assert.Equal("V", r.State.Vars["VERSION_VARIABLE"]);
    }

    [Fact]
    public async Task Checkout_version_puts_the_source_on_the_tag_of_the_detected_version_and_verify_notices_a_mismatch()
    {
        using var sb = new Sandbox();
        var repo = Tagged(sb);

        var before = await sb.RunStep("version", new() { ["verb"] = "verify-source", ["repo"] = repo });
        Assert.False(before.Ok);
        Assert.Contains("does not match the image", before.Text);

        var r = await sb.Run("checkout-version", repo);

        Assert.True(r.Ok, r.Text);
        Assert.Equal("v1.0.0", r.State.Vars["SOURCE_REF"]);
        Assert.Equal("print('plugin host')\n", File.ReadAllText(Path.Combine(sb.Plugin, "main.py")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Checkout_refuses_a_dirty_tree_and_an_unknown_version()
    {
        using var sb = new Sandbox();
        var repo = Tagged(sb);
        File.WriteAllText(Path.Combine(sb.Plugin, "main.py"), "edited\n");
        Assert.Contains("uncommitted changes", (await sb.RunStep("version", new() { ["verb"] = "checkout", ["repo"] = repo })).Text);
        Assert.Contains("none of these refs exist", (await sb.RunStep("version", new() { ["verb"] = "checkout", ["repo"] = repo, ["version"] = "9.9.9" })).Text);
    }
}
