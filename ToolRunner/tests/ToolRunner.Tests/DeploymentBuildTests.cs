using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using ToolRunner.Core;
using ToolRunner.Hosts;

namespace ToolRunner.Tests;

/// Building a deployment folder from product.json, a title, a version and the template; checking it; testing it.
[Collection("sandbox")]
public class DeploymentBuildTests
{
    private const string Product = """
        { "name": "Loyalty", "model": { "entities": [ { "name": "Customer", "fields": [] }, { "name": "Voucher" }, { "name": "Campaign" } ] } }
        """;

    private static (BuildSettings Settings, BuildPlan Plan, string Out) Build(Sandbox sb, params (string, string)[] set)
    {
        var product = Path.Combine(sb.Root, "product.json");
        File.WriteAllText(product, Product);
        var values = new Dictionary<string, string>
        {
            ["BUILD_PRODUCT_JSON"] = product, ["BUILD_TITLE"] = "loy", ["BUILD_VERSION"] = "2.4.1", ["BUILD_OUTPUT_DIR"] = Path.Combine(sb.Root, "deploy-loy"),
            ["BUILD_REGISTRY"] = "art.corp/docker-dev", ["BUILD_PORT_BASE"] = "9100"
        };
        foreach (var (k, v) in set) values[k] = v;
        var settings = BuildSettings.From(k => values.GetValueOrDefault(k));
        return (settings, DeploymentBuilder.Plan(settings, Repo.ShippedConfig, null), values["BUILD_OUTPUT_DIR"].Replace('\\', '/'));
    }

    [Theory]
    [InlineData("""{ "entities": [ { "name": "A" }, { "name": "B" } ] }""", null, "A,B")]
    [InlineData("""{ "product": { "entityTypes": [ "A", "B" ] } }""", null, "A,B")]
    [InlineData("""{ "schema": { "things": { "A": {}, "B": {} } } }""", "schema:things", "A,B")]
    [InlineData("""{ "pages": [ { "name": "Home" } ] }""", null, "")]
    public void Entities_are_read_from_product_json(string json, string? path, string expected) =>
        Assert.Equal(expected, string.Join(",", DeploymentBuilder.Entities(json, path)));

    [Fact]
    public void The_plan_says_what_is_still_to_choose()
    {
        var plan = DeploymentBuilder.Plan(BuildSettings.From(_ => null), Repo.ShippedConfig, null);
        Assert.Contains(plan.Problems, p => p.Contains("product.json"));
        Assert.Contains(plan.Problems, p => p.Contains("PRODUCT_TITLE"));
        Assert.Contains(plan.Problems, p => p.Contains("baseline version"));
        Assert.Contains(plan.Problems, p => p.Contains("deployment folder"));
    }

    [Fact]
    public void Everything_flows_from_the_product_file_the_title_and_the_version()
    {
        using var sb = new Sandbox();
        var (_, plan, _) = Build(sb);

        Assert.Empty(plan.Problems);
        Assert.Equal(["Customer", "Voucher", "Campaign"], plan.Entities);
        Assert.Equal("PRODUCT_TITLE=LOY\n", plan.Files["product.env"].Split('\n').First(l => l.StartsWith("PRODUCT_TITLE")) + "\n");
        Assert.Contains("PRODUCT_VERSION=2.4.1", plan.Files[".env"]);
        Assert.Contains("REGISTRY=art.corp/docker-dev", plan.Files[".env"]);
        Assert.Contains("KEYCLOAK_PORT=9100", plan.Files["keycloak/.env"]);                                        // Keycloak is a stack of its own
        Assert.Contains("KEYCLOAK_INTERNAL_URL=http://keycloak:8080", plan.Files[".env"]);
        Assert.DoesNotContain("  keycloak:", plan.Files["docker-compose.yml"]);
        Assert.Contains("external: true", plan.Files["docker-compose.yml"]);                                 // the product joins Keycloak's network
        Assert.Contains("WEBPORTAL_PORT=9102", plan.Files[".env"]);
        Assert.Contains("container_name: ${PRODUCT_TITLE}-webportal", plan.Files["docker-compose.yml"]);        // compose's own variables are untouched
        Assert.DoesNotContain("{{", string.Concat(plan.Files.Values));

        var settings = JsonNode.Parse(plan.Files["webportal/appsettings.json"])!;
        Assert.Equal("http://localhost:9100/realms/loy", (string?)settings["Authentication"]!["Authority"]);
        Assert.Equal("loy-webportal", (string?)settings["Authentication"]!["ClientId"]);
        Assert.Equal("http://keycloak:8080/realms/loy/.well-known/openid-configuration", (string?)settings["Authentication"]!["MetadataAddress"]);   // containers use the docker network
        Assert.Equal(["Acme.Portal.Widgets"], settings["Plugins"]!["Assemblies"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(Product, plan.Files["product.json"]);
        Assert.Contains("webportal/plugins", plan.Folders);
    }

    [Fact]
    public void The_keycloak_realm_has_the_clients_roles_users_and_a_privilege_string_naming_every_entity()
    {
        using var sb = new Sandbox();
        var (_, plan, _) = Build(sb);
        var realm = JsonNode.Parse(plan.Files["keycloak/import/realm.json"])!;

        Assert.Equal("loy", (string?)realm["realm"]);
        var portal = realm["clients"]!.AsArray().Single(c => (string?)c!["clientId"] == "loy-webportal")!;
        Assert.Equal("http://localhost:9102/*", (string?)portal["redirectUris"]![0]);
        Assert.Equal(["product-admin", "product-user"], realm["roles"]!["realm"]!.AsArray().Select(r => (string?)r!["name"]));
        var admin = realm["users"]!.AsArray().Single(u => (string?)u!["username"] == "loy-admin")!;
        Assert.Equal("product-admin", (string?)admin["realmRoles"]![0]);
        Assert.Equal("Customer:CRUD;Voucher:CRUD;Campaign:CRUD", (string?)admin["attributes"]!["privileges"]![0]);
        Assert.False((bool)admin["credentials"]![0]!["temporary"]!);
        Assert.Equal(plan.Users.Single(u => u.Username == "loy-admin").Password, (string?)admin["credentials"]![0]!["value"]);
        Assert.NotEqual(plan.Users[0].Password, plan.Users[1].Password);                                       // each user its own generated password
        Assert.Contains($"KEYCLOAK_ADMIN_PASSWORD={plan.Value("keycloak.adminPassword")}", plan.Files["keycloak/.env"]);
    }

    [Fact]
    public async Task The_written_folder_is_checked_like_any_deployment_and_passwords_stay_the_same_on_the_next_build()
    {
        using var sb = new Sandbox();
        var (_, plan, output) = Build(sb);

        await DeploymentBuilder.WriteAsync(plan, new LocalHost(), CancellationToken.None);
        var checks = await DeploymentBuilder.CheckAsync(output, new LocalHost(), CancellationToken.None);

        Assert.True(checks.Single(c => c.Name.StartsWith("every ${VARIABLE}")).Ok, checks.Single(c => c.Name.StartsWith("every ${VARIABLE}")).Detail);   // PRODUCT_TITLE comes from product.env
        Assert.True(checks.Single(c => c.Name.StartsWith("every bind-mount")).Ok, checks.Single(c => c.Name.StartsWith("every bind-mount")).Detail);
        Assert.True(checks.Single(c => c.Name.StartsWith("no host port")).Ok);
        var plugins = checks.Single(c => c.Name.StartsWith("every listed plugin"));
        Assert.False(plugins.Ok);                                                                               // not fetched yet
        Assert.Contains("Acme.Portal.Widgets", plugins.Detail);

        var (_, again, _) = Build(sb);
        Assert.Equal(plan.Value("keycloak.adminPassword"), again.Value("keycloak.adminPassword"));
        Assert.Equal(plan.Files[".env"], again.Files[".env"]);
    }

    [Fact]
    public async Task Plugin_packages_of_the_baseline_version_are_unpacked_into_each_services_folder()
    {
        using var sb = new Sandbox();
        var (_, plan, output) = Build(sb);
        await DeploymentBuilder.WriteAsync(plan, new LocalHost(), CancellationToken.None);
        byte[] Package(string id)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var entry in new[] { $"lib/net6.0/{id}.dll", $"lib/net8.0/{id}.dll", $"lib/net8.0/{id}.Helper.dll", $"{id}.nuspec" })
                    using (var w = new StreamWriter(zip.CreateEntry(entry).Open())) w.Write(entry);
            return ms.ToArray();
        }
        var server = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/2.4.1") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package(r.RequestUri.Segments[^2].TrimEnd('/'))) } : new HttpResponseMessage(HttpStatusCode.NotFound));

        var steps = await DeploymentBuilder.FetchPluginsAsync(plan, "https://art.corp/artifactory", "nuget-dev-local", "me", "t", CancellationToken.None, server);

        Assert.All(steps, s => Assert.True(s.Ok, s.Detail));
        Assert.Contains("/artifactory/api/nuget/nuget-dev-local/Download/Acme.Portal.Widgets/2.4.1", server.Paths);
        Assert.Equal("lib/net8.0/Acme.Portal.Widgets.dll", File.ReadAllText(Path.Combine(output, "webportal", "plugins", "Acme.Portal.Widgets.dll")));    // the newest framework
        Assert.True((await DeploymentBuilder.CheckAsync(output, new LocalHost(), CancellationToken.None)).All(c => c.Ok));                              // now nothing is missing
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Paths.Add(r.RequestUri!.AbsolutePath);
            Calls.Add($"{r.Method} {r.RequestUri.AbsolutePath} {(r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct))}");
            return answer(r);
        }
    }

    /// A Keycloak, a config service and a portal that behave as a correct deployment would.
    private static Stub Stack(BuildPlan plan, string portalPage, bool dropRole = false, bool existing = true)
    {
        var created = false;
        static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        var realm = JsonNode.Parse(plan.Files["keycloak/import/realm.json"])!;
        return new Stub(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            var port = r.RequestUri.Port;
            if (port == 9100)
            {
                if (path.EndsWith("/protocol/openid-connect/token")) return Json("""{ "access_token": "tok" }""");
                if (path == "/realms/master") return Json("{}");
                if (path == "/realms/loy") return existing || created ? Json("{}") : new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path == "/admin/realms" && r.Method == HttpMethod.Post) { created = true; return new HttpResponseMessage(HttpStatusCode.Created); }
                if (path.EndsWith("/clients")) return Json(realm["clients"]!.ToJsonString());
                if (path.EndsWith("/roles")) return Json(realm["roles"]!["realm"]!.ToJsonString());
                if (path.EndsWith("/users")) return Json(new JsonArray(realm["users"]!.AsArray().Select(u => { var c = u!.DeepClone(); c["id"] = (string?)u["username"]; return c; }).ToArray()).ToJsonString());
                if (path.Contains("/role-mappings/realm"))
                {
                    var user = realm["users"]!.AsArray().Single(u => path.Contains("/users/" + (string?)u!["username"] + "/"))!;
                    return Json(dropRole ? "[]" : new JsonArray(user["realmRoles"]!.AsArray().Select(x => (JsonNode)new JsonObject { ["name"] = (string?)x }).ToArray()).ToJsonString());
                }
                if (path == "/login-actions/authenticate") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(portalPage) };
            }
            if (port == 9101 && path == "/api/product" && r.Method == HttpMethod.Post && r.Headers.Authorization?.Parameter == "tok") return new HttpResponseMessage(HttpStatusCode.Created);
            if (port == 9102) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""<html><form id="kc-form-login" action="http://localhost:9100/login-actions/authenticate?session_code=x&amp;tab_id=y" method="post"><input name="username"/><input name="password" type="password"/></form></html>""") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task The_live_test_starts_the_stack_verifies_keycloak_deploys_the_product_and_reads_the_portal_as_the_user()
    {
        using var sb = new Sandbox();
        var (settings, plan, output) = Build(sb);
        await DeploymentBuilder.WriteAsync(plan, new LocalHost(), CancellationToken.None);
        var server = Stack(plan, "<h1>LOY</h1><a>View instances of Customer</a><a>View instances of Voucher</a><a>View instances of Campaign</a>");

        var steps = await DeploymentBuilder.TestAsync(plan, settings, new LocalHost(), CancellationToken.None, handler: server, waitSeconds: 0);

        Assert.All(steps, s => Assert.True(s.Ok, $"{s.Name}: {s.Detail}"));
        Assert.Equal("the portal shows every entity", steps[^1].Name);
        var up = sb.DockerCalls.Split('\n').Last(l => l.Contains(" up "));
        Assert.Contains("--env-file " + output + "/.env --env-file " + output + "/product.env", up.Replace("\"", ""));
        var ups = sb.DockerCalls.Split('\n').Where(l => l.Contains(" up ")).ToList();
        Assert.Contains(output + "/keycloak/docker-compose.yml", ups[^2]);                                     // Keycloak's stack first, then the product's
        Assert.Contains("imported at start-up", steps.Single(s => s.Name.StartsWith("Keycloak has the realm")).Detail);
        Assert.Contains(server.Calls, c => c.StartsWith("POST /realms/master/protocol/openid-connect/token") && c.Contains("client_id=admin-cli"));
        Assert.Contains(server.Calls, c => c.StartsWith("POST /realms/loy/protocol/openid-connect/token") && c.Contains("client_id=loy-productconfig") && c.Contains("username=loy-admin"));
        Assert.Contains(server.Calls, c => c.StartsWith("POST /api/product") && c.Contains("Voucher"));                                   // product.json itself was sent
        Assert.Contains(server.Calls, c => c.StartsWith("POST /login-actions/authenticate") && c.Contains("username=loy-user"));          // the end user, through the sign-in form
    }

    [Fact]
    public async Task The_live_test_fails_on_a_missing_entity_and_on_a_user_without_its_role()
    {
        using var sb = new Sandbox();
        var (settings, plan, _) = Build(sb);
        await DeploymentBuilder.WriteAsync(plan, new LocalHost(), CancellationToken.None);

        var page = await DeploymentBuilder.TestAsync(plan, settings, new LocalHost(), CancellationToken.None, handler: Stack(plan, "View instances of Customer, View instances of Voucher"), waitSeconds: 0, start: false);
        Assert.False(page[^1].Ok);
        Assert.Contains("not on the page: view instances of Campaign", page[^1].Detail);

        var roles = await DeploymentBuilder.TestAsync(plan, settings, new LocalHost(), CancellationToken.None, handler: Stack(plan, "", dropRole: true), waitSeconds: 0, start: false);
        Assert.Contains(roles, s => s.Name == "user loy-admin" && !s.Ok && s.Detail.Contains("roles missing: product-admin"));
        Assert.DoesNotContain(roles, s => s.Name == "product.json deployed");                                                            // it stops there
    }

    [Fact]
    public async Task A_keycloak_that_was_already_running_gets_the_realm_through_its_admin_api()
    {
        using var sb = new Sandbox();
        var (settings, plan, _) = Build(sb);
        var server = Stack(plan, "View instances of Customer, View instances of Voucher, View instances of Campaign", existing: false);

        var steps = await DeploymentBuilder.TestAsync(plan, settings, new LocalHost(), CancellationToken.None, handler: server, waitSeconds: 0, start: false);

        Assert.All(steps, s => Assert.True(s.Ok, $"{s.Name}: {s.Detail}"));
        Assert.Contains("created through the admin API", steps.Single(s => s.Name.StartsWith("Keycloak has the realm")).Detail);
        Assert.Contains(server.Calls, c => c.StartsWith("POST /admin/realms") && c.Contains("loy-webportal") && c.Contains("Customer:CRUD"));
    }

    [Fact]
    public void A_placeholder_nothing_supplies_is_a_problem_that_names_it()
    {
        using var sb = new Sandbox();
        var template = Path.Combine(sb.Root, "tpl");
        Directory.CreateDirectory(template);
        File.WriteAllText(Path.Combine(template, "template.json"), """{ "services": {}, "keycloak": { "url": "http://{{HOST}}:{{PORT+0}}", "roles": [], "users": [] } }""");
        File.WriteAllText(Path.Combine(template, ".env"), "REGION={{REGION}}\nTITLE={{PRODUCT_TITLE}}\n");

        Assert.Contains(Build(sb, ("BUILD_TEMPLATE_DIR", template)).Plan.Problems, p => p.Contains("{{REGION}}") && p.Contains("BUILD_VALUES"));
        var fixedPlan = Build(sb, ("BUILD_TEMPLATE_DIR", template), ("BUILD_VALUES", "REGION=eu")).Plan;
        Assert.Empty(fixedPlan.Problems);
        Assert.Equal("REGION=eu\nTITLE=LOY\n", fixedPlan.Files[".env"]);
    }
}
