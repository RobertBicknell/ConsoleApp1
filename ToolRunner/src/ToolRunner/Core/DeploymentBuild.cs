using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// What a new deployment is built from. Each value is a variable (BUILD_*), set in the UI, with --set, or in globals.user.json.
public sealed record BuildSettings(IReadOnlyList<PipelineSetting> All)
{
    public static readonly (string Var, string Label, string Help, string Default, string Group)[] Spec =
    [
        ("BUILD_PRODUCT_JSON", "product.json", "The product's data model file. Everything else flows from it.", "", "1. product"),
        ("BUILD_ENTITIES_PATH", "where the entities are", "Path inside product.json to the list of entities, e.g. model:entities. Empty = found automatically.", "", "1. product"),
        ("BUILD_TITLE", "PRODUCT_TITLE", "A short code for the product, about three letters: written to product.env, used as the prefix of container names.", "", "2. identity"),
        ("BUILD_REALM", "Keycloak realm", "Empty = the product title in lower case.", "", "2. identity"),
        ("BUILD_VERSION", "baseline version", "Selects the images and the plugin packages. Empty = the version in use (versions tab).", "", "3. version"),
        ("BUILD_REGISTRY", "image registry", "What precedes image names, e.g. HOST/docker-dev-local.", "registry.example.com", "3. version"),
        ("BUILD_NUGET_REPO", "plugin package repository", "Artifactory NuGet repository the plugin packages come from (with JFROG_URL / JFROG_USER / JFROG_TOKEN).", "", "3. version"),
        ("BUILD_TEMPLATE_DIR", "template folder", "docker-compose, .env, appsettings ... with {{PLACEHOLDERS}}, and template.json describing services, Keycloak and the test. Empty = the sample that ships with toolrunner.", "", "4. wiring"),
        ("BUILD_OUTPUT_DIR", "deployment folder to create", "Where the finished deployment is written. Existing files are overwritten; nothing is deleted.", "", "4. wiring"),
        ("BUILD_HOST", "host name in URLs", "The name browsers and services use to reach this machine.", "localhost", "4. wiring"),
        ("BUILD_PORT_BASE", "first port", "{{PORT+0}}, {{PORT+1}} ... in the templates count from here.", "8080", "4. wiring"),
        ("BUILD_VALUES", "more template values", "`;`-separated NAME=value pairs for placeholders of your own.", "", "4. wiring"),
        ("BUILD_PRIVILEGE_FORMAT", "privilege of one entity", "{entity} is replaced by each entity name.", "{entity}:CRUD", "5. keycloak"),
        ("BUILD_PRIVILEGE_SEPARATOR", "between privileges", "Joins the entities' privileges into the one string users carry.", ";", "5. keycloak"),
        ("BUILD_EXPECT", "text the portal must show per entity", "{entity} is replaced by each entity name; the test passes when all are on the page.", "view instances of {entity}", "6. test")
    ];

    public static BuildSettings From(Func<string, string?> var) =>
        new(Spec.Select(s => new PipelineSetting(s.Var, s.Label, s.Help, var(s.Var) is { Length: > 0 } v ? v : s.Default, s.Group)).ToList());

    public string this[string name] => All.First(s => s.Var == name).Value;
}

public sealed record BuildUser(string Username, string Password, IReadOnlyList<string> Roles, bool Privileges);
public sealed record BuildClient(string Service, string ClientId, string Url);

/// A deployment worked out but not yet written: the files (path in the folder -> content), the values the templates
/// were filled with, and everything that is not right yet. Nothing in Problems means it can be written.
public sealed record BuildPlan(string OutputDir, IReadOnlyList<string> Entities, IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Folders, IReadOnlyList<BuildClient> Clients, IReadOnlyList<BuildUser> Users, IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, List<string>> Plugins, JsonObject Manifest, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public string Value(string name) => Values.GetValueOrDefault(name, "");
    public string? Manifested(string path) => path.Split('.').Aggregate((JsonNode?)Manifest, (n, k) => n?[k]) is { } node ? ToolContext.NodeStr(node) : null;
}

public sealed record BuildStep(string Name, bool Ok, string Detail);

public static class DeploymentBuilder
{
    // ---------------------------------------------------------------- layer 1: the product's entities

    /// The names of the entities product.json defines. With a path ("model:entities") that list is used; without one,
    /// the first array of objects that carry a name, found under a property called entities, entityTypes, types or models.
    public static List<string> Entities(string productJson, string? path = null)
    {
        var root = JsonNode.Parse(productJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        static string? Name(JsonNode? n) => n switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonObject o => new[] { "name", "Name", "entityName", "id", "Id", "key" }.Select(k => ToolContext.NodeStr(o[k])).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
            _ => null
        };
        static List<string> Names(JsonNode? list) => list switch
        {
            JsonArray a => a.Select(Name).OfType<string>().ToList(),
            JsonObject o => o.Select(kv => kv.Key).ToList(),          // { "Customer": {...}, "Order": {...} }
            _ => new()
        };
        if (!string.IsNullOrWhiteSpace(path)) return Names(path.Split(':', '.', '/').Aggregate(root, (n, k) => n?[k]));

        JsonNode? Find(JsonNode? n, int depth) => n is not JsonObject o || depth > 4 ? null
            : o.FirstOrDefault(kv => Regex.IsMatch(kv.Key, "^(entities|entityTypes|types|models)$", RegexOptions.IgnoreCase) && Names(kv.Value).Count > 0).Value
              ?? o.Select(kv => Find(kv.Value, depth + 1)).FirstOrDefault(f => f is not null);
        return Names(Find(root, 0));
    }

    // ---------------------------------------------------------------- templates

    private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z_][\w.:+\-]*)\s*\}\}", RegexOptions.Compiled);

    /// Replaces {{NAME}}. Special forms: {{PORT+n}} (first port + n), {{SECRET:name}} (a generated password, kept stable
    /// between runs), {{PLUGINS:service}} (the service's plugin assemblies as JSON strings: "A", "B").
    /// Compose's own ${VAR} is left alone. Names nothing supplies are collected in `unresolved` and left in the text.
    public static string Render(string text, IReadOnlyDictionary<string, string> values, Dictionary<string, string> secrets, ICollection<string> unresolved) =>
        Placeholder.Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            if (values.TryGetValue(name, out var v)) return v;
            if (name.StartsWith("PORT+") && int.TryParse(name[5..], out var n) && int.TryParse(values.GetValueOrDefault("PORT_BASE"), out var first)) return (first + n).ToString();
            if (name.StartsWith("SECRET:"))
            {
                if (!secrets.TryGetValue(name[7..], out var secret)) secrets[name[7..]] = secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                return secret;
            }
            if (!unresolved.Contains(name)) unresolved.Add(name);
            return m.Value;
        });

    public static string DefaultTemplateDir(string configDir) => Path.Combine(configDir, "deployment-template");

    private static string SecretsPath(string outputDir) => Path.Combine(outputDir, ".toolrunner", "build-secrets.json");

    // ---------------------------------------------------------------- layers 2-5: the plan

    public static BuildPlan Plan(BuildSettings s, string configDir, string? effectiveVersion)
    {
        var problems = new List<string>(); var notes = new List<string>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var folders = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = s["BUILD_OUTPUT_DIR"].Replace('\\', '/').TrimEnd('/');
        BuildPlan Done(List<string> entities, JsonObject? manifest = null, List<BuildClient>? clients = null, List<BuildUser>? users = null, List<string>? roles = null, Dictionary<string, List<string>>? plugins = null) =>
            new(output, entities, values, files, folders, clients ?? [], users ?? [], roles ?? [], plugins ?? new(), manifest ?? new JsonObject(), problems, notes);

        // 1. product.json -> entities
        var entities = new List<string>();
        var productText = "";
        if (s["BUILD_PRODUCT_JSON"].Length == 0) problems.Add("choose the product.json (BUILD_PRODUCT_JSON)");
        else if (!File.Exists(s["BUILD_PRODUCT_JSON"])) problems.Add($"product.json not found: {s["BUILD_PRODUCT_JSON"]}");
        else
            try
            {
                productText = File.ReadAllText(s["BUILD_PRODUCT_JSON"]);
                entities = Entities(productText, s["BUILD_ENTITIES_PATH"]);
                if (entities.Count == 0) problems.Add("no entities found in product.json; say where they are with BUILD_ENTITIES_PATH (e.g. model:entities)");
            }
            catch (JsonException e) { problems.Add($"product.json is not valid JSON: {e.Message}"); }

        // 2. title, 3. version
        var title = s["BUILD_TITLE"].Trim();
        if (title.Length == 0) problems.Add("choose a PRODUCT_TITLE (BUILD_TITLE), e.g. three letters");
        else if (!Regex.IsMatch(title, "^[A-Za-z][A-Za-z0-9]{1,11}$")) problems.Add($"PRODUCT_TITLE '{title}' must be 2-12 letters or digits starting with a letter (it becomes part of container names)");
        var version = s["BUILD_VERSION"].Length > 0 ? s["BUILD_VERSION"] : effectiveVersion ?? "";
        if (version.Length == 0) problems.Add("choose the baseline version (BUILD_VERSION)");
        if (output.Length == 0) problems.Add("choose the deployment folder to create (BUILD_OUTPUT_DIR)");

        var privileges = string.Join(s["BUILD_PRIVILEGE_SEPARATOR"], entities.Select(e => s["BUILD_PRIVILEGE_FORMAT"].Replace("{entity}", e)));
        foreach (var (k, v) in new (string, string)[]
        {
            ("PRODUCT_TITLE", title.ToUpperInvariant()), ("product_title", title.ToLowerInvariant()), ("VERSION", version), ("REGISTRY", s["BUILD_REGISTRY"].TrimEnd('/')),
            ("HOST", s["BUILD_HOST"]), ("PORT_BASE", s["BUILD_PORT_BASE"]), ("REALM", s["BUILD_REALM"].Length > 0 ? s["BUILD_REALM"] : title.ToLowerInvariant()),
            ("ENTITIES", string.Join(",", entities)), ("PRIVILEGES", privileges)
        }) values[k] = v;
        foreach (var pair in s["BUILD_VALUES"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (pair.IndexOf('=') is > 0 and var eq) values[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();

        // 4. the template folder and its manifest
        var templateDir = s["BUILD_TEMPLATE_DIR"].Length > 0 ? s["BUILD_TEMPLATE_DIR"] : DefaultTemplateDir(configDir);
        var manifestPath = Path.Combine(templateDir, "template.json");
        if (!File.Exists(manifestPath)) { problems.Add($"no template.json in the template folder {templateDir}"); return Done(entities); }

        var secrets = new Dictionary<string, string>();
        if (output.Length > 0 && File.Exists(SecretsPath(output)))
            try { secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SecretsPath(output))) ?? new(); } catch (JsonException) { notes.Add("the saved secrets could not be read; new ones were generated"); }
        var before = secrets.Count;
        var unresolved = new List<string>();
        string Fill(string text) => Render(text, values, secrets, unresolved);

        JsonObject manifest;
        try { manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!; }
        catch (Exception e) when (e is JsonException or InvalidCastException) { problems.Add($"template.json is not valid: {e.Message}"); return Done(entities); }

        foreach (var (k, v) in manifest["values"] as JsonObject ?? []) if (!values.ContainsKey(k)) values[k] = Fill(ToolContext.NodeStr(v) ?? "");

        // services first: their URLs and plugin lists become values the other templates use ({{webportal.url}}, {{PLUGINS:webportal}})
        var clients = new List<BuildClient>(); var plugins = new Dictionary<string, List<string>>();
        foreach (var (name, node) in manifest["services"] as JsonObject ?? [])
        {
            var url = Fill(ToolContext.NodeStr(node?["url"]) ?? "");
            values[$"{name}.url"] = url;
            var clientId = Fill(ToolContext.NodeStr(node?["client"]) ?? "");
            if (clientId.Length > 0) { values[$"{name}.client"] = clientId; clients.Add(new BuildClient(name, clientId, url)); }
            var list = (node?["plugins"] as JsonArray ?? []).Select(p => Fill(ToolContext.NodeStr(p) ?? "")).Where(p => p.Length > 0).ToList();
            plugins[name] = list;
            values[$"PLUGINS:{name}"] = string.Join(", ", list.Select(p => JsonSerializer.Serialize(p)));
            if (ToolContext.NodeStr(node?["pluginsDir"]) is { Length: > 0 } dir) { folders.Add(Fill(dir)); values[$"{name}.pluginsDir"] = Fill(dir); }
        }
        var kc = manifest["keycloak"] as JsonObject ?? new JsonObject();
        values["keycloak.url"] = Fill(ToolContext.NodeStr(kc["url"]) ?? "");
        // containers reach Keycloak over the docker network it shares with them; browsers use the published URL
        values["keycloak.internalUrl"] = Fill(ToolContext.NodeStr(kc["internalUrl"]) ?? ToolContext.NodeStr(kc["url"]) ?? "");
        values["keycloak.admin"] = Fill(ToolContext.NodeStr(kc["admin"]) ?? "admin");
        values["keycloak.adminPassword"] = Fill(ToolContext.NodeStr(kc["adminPassword"]) ?? "{{SECRET:keycloak-admin}}");
        var roles = (kc["roles"] as JsonArray ?? []).Select(r => Fill(ToolContext.NodeStr(r) ?? "")).ToList();
        var users = (kc["users"] as JsonArray ?? []).OfType<JsonObject>().Select(u => new BuildUser(Fill(ToolContext.NodeStr(u["username"]) ?? ""),
            Fill(ToolContext.NodeStr(u["password"]) ?? $"{{{{SECRET:{ToolContext.NodeStr((u["roles"] as JsonArray)?.FirstOrDefault()) ?? "user"}}}}}"),
            (u["roles"] as JsonArray ?? []).Select(r => Fill(ToolContext.NodeStr(r) ?? "")).ToList(), u["privileges"]?.GetValueKind() == JsonValueKind.True)).ToList();
        foreach (var u in users)
        {
            values[$"user.{u.Roles.FirstOrDefault() ?? u.Username}.name"] = u.Username;
            values[$"user.{u.Roles.FirstOrDefault() ?? u.Username}.password"] = u.Password;
            foreach (var missing in u.Roles.Where(r => !roles.Contains(r))) problems.Add($"template.json: user {u.Username} has the role '{missing}', which keycloak.roles does not define");
        }

        // every file of the template, filled in (names may carry placeholders too)
        foreach (var file in Directory.EnumerateFiles(templateDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(templateDir, file).Replace('\\', '/');
            if (rel is "template.json" or "README.md") continue;
            files[Fill(rel)] = Fill(File.ReadAllText(file));
        }

        // 5. Keycloak: the realm it imports at start-up, unless the template brought its own file at that path
        var importFile = Fill(ToolContext.NodeStr(kc["importFile"]) ?? "keycloak/import/realm.json");
        if (!files.ContainsKey(importFile)) files[importFile] = Realm(values["REALM"], clients, roles, users, privileges, ToolContext.NodeStr(kc["privilegeAttribute"]) ?? "privileges");
        else notes.Add($"{importFile} comes from the template; the generated realm was not used");
        if (productText.Length > 0) files[Fill(ToolContext.NodeStr(manifest["productFile"]) ?? "product.json")] = productText;

        foreach (var name in unresolved) problems.Add($"the templates use {{{{{name}}}}} but nothing supplies it (add {name}=... to BUILD_VALUES, or define it in template.json)");
        if (secrets.Count > before) notes.Add($"{secrets.Count - before} password(s) were generated; they are kept in .toolrunner/build-secrets.json so the next build reuses them");
        values["__secrets"] = JsonSerializer.Serialize(secrets);
        return Done(entities, manifest, clients, users, roles, plugins);
    }

    /// A Keycloak realm in the form `--import-realm` reads: clients for the apps, roles, and users with their roles and
    /// the privilege string (one attribute naming every entity).
    public static string Realm(string realm, IEnumerable<BuildClient> clients, IEnumerable<string> roles, IEnumerable<BuildUser> users, string privileges, string attribute = "privileges") =>
        new JsonObject
        {
            ["realm"] = realm, ["enabled"] = true,
            ["roles"] = new JsonObject { ["realm"] = new JsonArray(roles.Select(r => (JsonNode)new JsonObject { ["name"] = r }).ToArray()) },
            ["clients"] = new JsonArray(clients.Select(c => (JsonNode)new JsonObject
            {
                ["clientId"] = c.ClientId, ["enabled"] = true, ["publicClient"] = true, ["standardFlowEnabled"] = true, ["directAccessGrantsEnabled"] = true,
                ["rootUrl"] = c.Url, ["redirectUris"] = new JsonArray(c.Url.TrimEnd('/') + "/*"), ["webOrigins"] = new JsonArray(c.Url.TrimEnd('/')),
                ["attributes"] = new JsonObject { ["post.logout.redirect.uris"] = c.Url.TrimEnd('/') + "/*" }
            }).ToArray()),
            ["users"] = new JsonArray(users.Select(u =>
            {
                var o = new JsonObject
                {
                    ["username"] = u.Username, ["enabled"] = true, ["emailVerified"] = true, ["firstName"] = u.Username, ["lastName"] = realm, ["email"] = $"{u.Username}@{realm}.local",
                    ["credentials"] = new JsonArray(new JsonObject { ["type"] = "password", ["value"] = u.Password, ["temporary"] = false }),
                    ["realmRoles"] = new JsonArray(u.Roles.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
                };
                if (u.Privileges) o["attributes"] = new JsonObject { [attribute] = new JsonArray(privileges) };
                return (JsonNode)o;
            }).ToArray())
        }.ToJsonString(ConfigLoader.JsonOptions) + "\n";

    // ---------------------------------------------------------------- writing, fetching plugins, checking

    public static async Task<List<string>> WriteAsync(BuildPlan plan, IHost host, CancellationToken ct)
    {
        if (plan.Problems.Count > 0) throw new InvalidOperationException($"the plan has {plan.Problems.Count} problem(s); nothing was written");
        var written = new List<string>();
        foreach (var (rel, content) in plan.Files) { await host.WriteTextAsync($"{plan.OutputDir}/{rel}", content, ct); written.Add(rel); }
        foreach (var folder in plan.Folders) await host.WriteTextAsync($"{plan.OutputDir}/{folder.TrimEnd('/')}/.gitkeep", "", ct);
        await host.WriteTextAsync($"{plan.OutputDir}/.toolrunner/build-secrets.json", plan.Value("__secrets"), ct);
        await host.WriteTextAsync($"{plan.OutputDir}/.toolrunner/.gitignore", "*\n", ct);
        return written;
    }

    /// Downloads each service's plugin packages at the baseline version from Artifactory and unpacks their assemblies
    /// (lib/*/*) into the service's plugins folder. Local folders only.
    public static async Task<List<BuildStep>> FetchPluginsAsync(BuildPlan plan, string jfrogUrl, string repo, string? user, string? token, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var steps = new List<BuildStep>();
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        if (!string.IsNullOrEmpty(user)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}")));
        else if (!string.IsNullOrEmpty(token)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var (service, ids) in plan.Plugins)
            foreach (var id in ids)
            {
                var url = $"{jfrogUrl.TrimEnd('/')}/api/nuget/{repo}/Download/{id}/{plan.Value("VERSION")}";
                try
                {
                    using var r = await http.GetAsync(url, ct);
                    if (!r.IsSuccessStatusCode) { steps.Add(new($"{service}: {id} {plan.Value("VERSION")}", false, $"HTTP {(int)r.StatusCode} from {url}")); continue; }
                    using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync(ct)));
                    var dir = Path.Combine(plan.OutputDir, plan.Value($"{service}.pluginsDir"));
                    Directory.CreateDirectory(dir);
                    // one target framework only: the newest lib folder (names sort that way for net6.0 / net8.0 / net9.0 by length then text)
                    var tfm = zip.Entries.Where(e => e.FullName.StartsWith("lib/") && e.Name.Length > 0).Select(e => e.FullName.Split('/')[1]).Distinct().OrderBy(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).LastOrDefault();
                    var copied = 0;
                    foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith($"lib/{tfm}/") && e.Name.Length > 0)) { e.ExtractToFile(Path.Combine(dir, e.Name), overwrite: true); copied++; }
                    steps.Add(new($"{service}: {id} {plan.Value("VERSION")}", copied > 0, copied > 0 ? $"{copied} file(s) from lib/{tfm} into {plan.Value($"{service}.pluginsDir")}" : "the package has no lib folder"));
                }
                catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or TaskCanceledException) { steps.Add(new($"{service}: {id}", false, $"{url}: {e.Message}")); }
            }
        return steps;
    }

    /// Reads the written folder back the way the scanner reads any deployment and reports what a start would trip over:
    /// variables nothing defines, bind-mount sources that do not exist, a host port used twice, listed plugins without a file.
    public static async Task<List<BuildStep>> CheckAsync(string outputDir, IHost host, CancellationToken ct)
    {
        var scan = await StackScanner.ScanDirectoryAsync(host, outputDir, 4, ct);
        var steps = new List<BuildStep>();
        if (scan.Stacks.Count == 0) return [new("compose file", false, $"no compose file found in {outputDir}")];
        // product.env is handed to compose with a second --env-file; the scanner only knows .env
        var productEnv = await host.ReadTextAsync($"{outputDir}/product.env", ct) is { } text ? Scanner.EnvFile("product.env", text, "env").Select(v => v.Name).ToHashSet() : [];
        string In(Stack st) => scan.Stacks.Count == 1 ? "" : (st.Dir.Length > outputDir.Length ? st.Dir[(outputDir.TrimEnd('/').Length + 1)..] : ".") + ": ";
        var undefined = scan.Stacks.SelectMany(st => st.Variables.Where(v => v.Status == "undefined" && !productEnv.Contains(v.Name)).Select(v => In(st) + v.Name)).ToList();
        steps.Add(new("every ${VARIABLE} has a value", undefined.Count == 0, undefined.Count == 0 ? $"{scan.Stacks.Sum(st => st.Variables.Count)} variable(s) in {scan.Stacks.Count} stack(s)" : "undefined: " + string.Join(", ", undefined)));
        var missing = scan.Stacks.SelectMany(st => st.Mounts.Where(m => m.Kind == "bind" && m.Exists == false).Select(m => $"{In(st)}{m.Service}: {m.Source}")).ToList();
        steps.Add(new("every bind-mount source exists", missing.Count == 0, missing.Count == 0 ? $"{scan.Stacks.Sum(st => st.Mounts.Count(m => m.Kind == "bind"))} bind mount(s)" : "missing: " + string.Join("; ", missing)));
        var lost = scan.Stacks.SelectMany(st => st.Plugins.SelectMany(p => p.Items.Where(i => i.FoundAt is null).Select(i => $"{p.Service}: {i.Name}"))).ToList();
        steps.Add(new("every listed plugin assembly is in its folder", lost.Count == 0, lost.Count == 0 ? $"{scan.Stacks.Sum(st => st.Plugins.Sum(p => p.Items.Count))} listed" : "not there yet (fetch the plugins): " + string.Join("; ", lost)));
        var imageless = scan.Stacks.SelectMany(st => st.Services.Where(sv => sv.Image is null && !sv.HasBuild).Select(sv => In(st) + sv.Name)).ToList();
        steps.Add(new("every service has an image", imageless.Count == 0, imageless.Count == 0 ? string.Join(", ", scan.Stacks.SelectMany(st => st.Services.Select(sv => sv.Image))) : "no image: " + string.Join(", ", imageless)));
        steps.Add(new("no host port is used twice", scan.Conflicts.Count == 0, scan.Conflicts.Count == 0 ? "" : string.Join("; ", scan.Conflicts.Select(c => $"{c.Published}: {string.Join(" and ", c.Users)}"))));
        return steps;
    }

    // ---------------------------------------------------------------- the live test

    private static async Task<(int Status, string Body)> Send(HttpClient http, HttpMethod method, string url, HttpContent? content, string? bearer, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url) { Content = content };
            if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var r = await http.SendAsync(req, ct);
            return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (0, e.Message); }
    }

    private static async Task<(string? Token, string Detail)> Token(HttpClient http, string keycloak, string realm, string client, string user, string password, CancellationToken ct)
    {
        var (status, body) = await Send(http, HttpMethod.Post, $"{keycloak.TrimEnd('/')}/realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password", ["client_id"] = client, ["username"] = user, ["password"] = password }), null, ct);
        string? token = null;
        try { token = status == 200 ? ToolContext.NodeStr(JsonNode.Parse(body)?["access_token"]) : null; } catch (JsonException) { }
        return (token, token is not null ? "signed in" : $"HTTP {status}: {(body.Length > 200 ? body[..200] : body)}");
    }

    /// Starts the stack and proves the chain end to end: Keycloak holds the realm, clients, roles and users; the product
    /// admin can deploy product.json; the portal shows every entity to the end user. Stops at the first failed step.
    public static async Task<List<BuildStep>> TestAsync(BuildPlan plan, BuildSettings s, IHost host, CancellationToken ct, Action<BuildStep>? progress = null,
        HttpMessageHandler? handler = null, int waitSeconds = 300, bool start = true)
    {
        var steps = new List<BuildStep>();
        bool Add(string name, bool ok, string detail) { var step = new BuildStep(name, ok, detail); steps.Add(step); progress?.Invoke(step); return ok; }
        using var http = new HttpClient(handler ?? new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
        var (kc, realm) = (plan.Value("keycloak.url"), plan.Value("REALM"));

        // 1. docker compose up for each stack of the deployment, in the template's order (Keycloak is its own stack)
        if (start)
        {
            var stacks = (plan.Manifest["stacks"] as JsonArray ?? ["."]).Select(x => ToolContext.NodeStr(x) ?? ".").ToList();
            foreach (var stack in stacks)
            {
                var dir = stack is "." or "" ? plan.OutputDir : $"{plan.OutputDir}/{stack.Trim('/')}";
                var args = new List<string> { "compose", "--project-directory", dir, "-f", $"{dir}/docker-compose.yml", "--env-file", $"{dir}/.env" };
                if (await host.ExistsAsync($"{dir}/product.env", ct)) { args.Add("--env-file"); args.Add($"{dir}/product.env"); }
                args.AddRange(["up", "-d"]);
                var up = await host.ExecAsync("docker", args, null, null, ct);
                if (!Add($"docker compose up ({(stack is "." or "" ? "product" : stack)})", up.Code == 0, up.Code == 0 ? "started" : up.Stderr.Trim().Split('\n').LastOrDefault() ?? $"exit {up.Code}")) return steps;
            }
        }

        // 2. Keycloak answers, and its admin can sign in
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        (int Status, string Body) alive;
        do { alive = await Send(http, HttpMethod.Get, $"{kc.TrimEnd('/')}/realms/master", null, null, ct); if (alive.Status == 200) break; await Task.Delay(waitSeconds > 0 ? 3000 : 1, ct); }
        while (DateTime.UtcNow < deadline);
        if (!Add("Keycloak answers", alive.Status == 200, alive.Status == 200 ? kc : $"{kc}/realms/master: {(alive.Status == 0 ? alive.Body : "HTTP " + alive.Status)} after {waitSeconds}s")) return steps;
        var (admin, adminDetail) = await Token(http, kc, "master", "admin-cli", plan.Value("keycloak.admin"), plan.Value("keycloak.adminPassword"), ct);
        if (!Add("Keycloak admin signs in", admin is not null, adminDetail)) return steps;

        // 3. the realm: imported at start-up when Keycloak started fresh; created through the admin API when Keycloak was already running
        var importFile = Render(plan.Manifested("keycloak.importFile") ?? "keycloak/import/realm.json", plan.Values, new(), new List<string>());
        var realmAnswer = await Send(http, HttpMethod.Get, $"{kc.TrimEnd('/')}/realms/{realm}", null, null, ct);
        var how = "imported at start-up";
        if (realmAnswer.Status == 404 && plan.Files.TryGetValue(importFile, out var realmJson))
        {
            var created = await Send(http, HttpMethod.Post, $"{kc.TrimEnd('/')}/admin/realms", new StringContent(realmJson, Encoding.UTF8, "application/json"), admin, ct);
            how = created.Status is >= 200 and < 300 ? "created through the admin API (Keycloak was already running)" : $"could not be created: HTTP {created.Status} {(created.Body.Length > 160 ? created.Body[..160] : created.Body)}";
            realmAnswer = await Send(http, HttpMethod.Get, $"{kc.TrimEnd('/')}/realms/{realm}", null, null, ct);
        }
        if (!Add($"Keycloak has the realm '{realm}'", realmAnswer.Status == 200, realmAnswer.Status == 200 ? how : $"HTTP {realmAnswer.Status}; {how}")) return steps;

        // 4. with the admin account: clients, roles, users, their roles and privileges
        async Task<JsonArray> List(string path) { var (st, body) = await Send(http, HttpMethod.Get, $"{kc.TrimEnd('/')}/admin/realms/{realm}/{path}", null, admin, ct); try { return st == 200 ? JsonNode.Parse(body) as JsonArray ?? [] : []; } catch (JsonException) { return []; } }
        var haveClients = (await List("clients")).Select(c => ToolContext.NodeStr(c?["clientId"])).ToList();
        var lostClients = plan.Clients.Where(c => !haveClients.Contains(c.ClientId)).Select(c => c.ClientId).ToList();
        Add("clients imported", lostClients.Count == 0, lostClients.Count == 0 ? string.Join(", ", plan.Clients.Select(c => c.ClientId)) : "missing: " + string.Join(", ", lostClients));
        var haveRoles = (await List("roles")).Select(r => ToolContext.NodeStr(r?["name"])).ToList();
        var lostRoles = plan.Roles.Where(r => !haveRoles.Contains(r)).ToList();
        Add("roles imported", lostRoles.Count == 0, lostRoles.Count == 0 ? string.Join(", ", plan.Roles) : "missing: " + string.Join(", ", lostRoles));
        var haveUsers = await List("users?max=500");
        foreach (var u in plan.Users)
        {
            var found = haveUsers.FirstOrDefault(x => ToolContext.NodeStr(x?["username"])?.Equals(u.Username, StringComparison.OrdinalIgnoreCase) == true);
            if (found is null) { Add($"user {u.Username}", false, "not in the realm"); continue; }
            var mapped = (await List($"users/{ToolContext.NodeStr(found["id"])}/role-mappings/realm")).Select(r => ToolContext.NodeStr(r?["name"])).ToList();
            var attributes = string.Join(" ", (found["attributes"] as JsonObject ?? []).SelectMany(a => a.Value as JsonArray ?? []).Select(v => ToolContext.NodeStr(v)));
            var noRole = u.Roles.Where(r => !mapped.Contains(r)).ToList();
            var noEntity = u.Privileges ? plan.Entities.Where(e => !attributes.Contains(e)).ToList() : [];
            Add($"user {u.Username}", noRole.Count + noEntity.Count == 0, noRole.Count + noEntity.Count == 0 ? $"roles {string.Join(", ", u.Roles)}{(u.Privileges ? $"; privileges name all {plan.Entities.Count} entities" : "")}"
                : $"{(noRole.Count > 0 ? "roles missing: " + string.Join(", ", noRole) + ". " : "")}{(noEntity.Count > 0 ? "privileges do not mention: " + string.Join(", ", noEntity) : "")}");
        }
        if (steps.Any(x => !x.Ok)) return steps;

        // 4. the product admin deploys product.json
        var test = plan.Manifest["test"] as JsonObject ?? new JsonObject();
        string T(string key, string fallback = "") => Render(ToolContext.NodeStr(test[key]) ?? fallback, plan.Values, new(), new List<string>());
        var (adminUser, portalUser) = (plan.Users.FirstOrDefault(u => u.Username == T("deployUser")) ?? plan.Users.FirstOrDefault(), plan.Users.FirstOrDefault(u => u.Username == T("portalUser")) ?? plan.Users.LastOrDefault());
        if (adminUser is null || portalUser is null) { Add("users for the test", false, "template.json defines no users"); return steps; }
        var (token, tokenDetail) = await Token(http, kc, realm, T("deployClient"), adminUser.Username, adminUser.Password, ct);
        if (!Add($"{adminUser.Username} signs in to {T("deployClient")}", token is not null, tokenDetail)) return steps;
        var productFile = plan.Files.GetValueOrDefault(ToolContext.NodeStr(plan.Manifest["productFile"]) ?? "product.json", "{}");
        var deploy = await Send(http, new HttpMethod(T("deployMethod", "POST")), T("deployUrl"), new StringContent(productFile, Encoding.UTF8, "application/json"), token, ct);
        if (!Add("product.json deployed", deploy.Status is >= 200 and < 300, $"{T("deployMethod", "POST")} {T("deployUrl")}: {(deploy.Status == 0 ? deploy.Body : "HTTP " + deploy.Status)}")) return steps;

        // 5. the end user opens the portal: through the Keycloak sign-in form, then the page must name every entity
        var page = await Send(http, HttpMethod.Get, T("portalUrl"), null, null, ct);
        if (Regex.Match(page.Body, @"<form[^>]*action=""([^""]+)""", RegexOptions.IgnoreCase) is { Success: true } form && page.Body.Contains("password", StringComparison.OrdinalIgnoreCase))
            page = await Send(http, HttpMethod.Post, System.Net.WebUtility.HtmlDecode(form.Groups[1].Value),
                new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = portalUser.Username, ["password"] = portalUser.Password, ["credentialId"] = "" }), null, ct);
        if (!Add($"{portalUser.Username} opens the portal", page.Status == 200, $"{T("portalUrl")}: {(page.Status == 0 ? page.Body : "HTTP " + page.Status)}")) return steps;
        var absent = plan.Entities.Where(e => !page.Body.Contains(s["BUILD_EXPECT"].Replace("{entity}", e), StringComparison.OrdinalIgnoreCase)).ToList();
        Add("the portal shows every entity", absent.Count == 0, absent.Count == 0 ? string.Join("; ", plan.Entities.Select(e => s["BUILD_EXPECT"].Replace("{entity}", e)))
            : $"not on the page: {string.Join("; ", absent.Select(e => s["BUILD_EXPECT"].Replace("{entity}", e)))}{(page.Body.Contains("<script", StringComparison.OrdinalIgnoreCase) ? " (the page runs scripts: if it draws its content in the browser, this check needs a browser test instead)" : "")}");
        return steps;
    }
}
