using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner.Core;

/// Generates JSON Schemas (draft-07) for the three config files from what the code knows, so Visual Studio's JSON
/// editor offers completion, hover docs and error squiggles: tool names, verbs per tool, the arguments each verb takes,
/// and (for workflow steps) the arguments of the verb behind the named arg set.
/// The schemas are derived data: regenerate with `toolrunner schema` (the UI does it whenever a config file changes).
/// Any value may be a `${...}` expression, so every typed property also accepts a string, and verbs may be `${...}`.
public static class Schemas
{
    private const string Draft = "http://json-schema.org/draft-07/schema#";
    private const string Expr = @"\$\{";     // a value containing ${...} is checked at run time, not here

    private static JsonObject O(params (string Key, JsonNode? Value)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) if (v is not null) o[k] = v;
        return o;
    }
    private static JsonArray Arr(params JsonNode?[] items) => new(items);
    private static JsonArray Types(params string[] t) => new(t.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
    private static JsonObject Str(string? doc = null) => O(("type", "string"), ("description", doc));
    private static JsonObject Ref(string name) => O(("$ref", $"#/definitions/{name}"));

    // ---- argument types ----

    private static JsonObject Prop(Arg a)
    {
        var p = a.Type switch
        {
            "bool" => O(("type", Types("boolean", "string"))),
            "int" => O(("type", Types("integer", "string"))),
            "list" => O(("type", Types("array", "string")), ("items", O(("type", "string")))),
            "object" => O(("type", "object"), ("additionalProperties", O(("type", Types("string", "number", "boolean"))))),
            "edits" => O(("type", "array"), ("minItems", 1), ("items", Edit())),
            _ when a.Values is { } v => O(("anyOf", Arr(O(("enum", new JsonArray(v.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()))), O(("pattern", Expr))))),
            _ => O(("type", "string"))
        };
        p["description"] = a.Doc;
        return p;
    }

    // The three shapes FileTool.derive accepts; each key is documented where it is used.
    private static JsonObject Edit() => O(
        ("type", "object"),
        ("properties", O(
            ("match", Str("Text to replace inside the matching line(s).")), ("replace", Str("Replacement text (with match).")),
            ("expect", O(("type", Types("integer", "string")), ("description", "How many lines must match (default 1)."))),
            ("set", Str("Env-file KEY whose KEY=... line is replaced.")), ("value", Str("New value (with set).")),
            ("addIfMissing", O(("type", Types("boolean", "string")), ("description", "Append KEY=value if the key is absent."))),
            ("commentService", Str("Service name to comment out under services:.")),
            ("dropDependsOn", Str("Service name: comment out every other service's depends_on entry for it.")),
            ("ignoreComments", O(("type", Types("boolean", "string")), ("description", "Set false to let commented-out lines match."))))),
        ("additionalProperties", false));

    private static JsonObject VerbProps(VerbSpec v)
    {
        var props = O(("verb", true));
        foreach (var a in v.Args) props[a.Name] = Prop(a);
        return props;
    }

    // ---- args.json ----

    private static JsonObject ToolDefinition(ITool tool)
    {
        var union = new JsonObject();
        foreach (var v in tool.Spec) foreach (var a in v.Args) union.TryAdd(a.Name, Prop(a));

        var verbs = tool.Spec.Select(v => (JsonNode)O(("const", v.Verb), ("description", v.Doc))).Append(O(("pattern", Expr))).ToArray();
        union["verb"] = O(("anyOf", new JsonArray(verbs)), ("description", $"What `{tool.Name}` does."));
        var perVerb = tool.Spec.Select(v =>
        {
            var then = O(("properties", VerbProps(v)), ("additionalProperties", false));
            if (v.Args.Where(a => a.Required).Select(a => (JsonNode)JsonValue.Create(a.Name)).ToArray() is { Length: > 0 } req) then["required"] = new JsonArray(req);
            return (JsonNode)O(("if", O(("properties", O(("verb", O(("const", v.Verb))))), ("required", Arr("verb")))), ("then", then));
        }).ToArray();

        return O(("type", "object"), ("required", Arr("verb")), ("properties", union), ("allOf", new JsonArray(perVerb)));
    }

    private static JsonObject ArgsSchema(ToolRegistry tools)
    {
        var defs = new JsonObject();
        var toolProps = new JsonObject();
        foreach (var t in tools.All)
        {
            defs[$"tool_{t.Name}"] = ToolDefinition(t);
            toolProps[t.Name] = O(("type", "object"), ("description", $"Named argument sets for the `{t.Name}` tool; each key is an intent."),
                                  ("additionalProperties", Ref($"tool_{t.Name}")));
        }
        return O(("$schema", Draft), ("title", "toolrunner args.json"), ("type", "object"),
                 ("properties", O(("$schema", Str()), ("args", O(("type", "object"), ("properties", toolProps), ("additionalProperties", false))))),
                 ("definitions", defs));
    }

    // ---- workflows.json ----

    private static JsonObject WorkflowsSchema(ToolRegistry tools, ConfigSet cfg)
    {
        // every "tool/intent" that args.json defines, plus bare tool names (verb then comes from `with`)
        var intents = cfg.Args.SelectMany(t => t.Value.Keys.Select(i => $"{t.Key}/{i}")).Order().Concat(tools.All.Select(t => t.Name)).ToList();
        var run = O(("anyOf", Arr(O(("enum", new JsonArray(intents.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()))), O(("pattern", Expr)))),
                    ("description", "tool/intent (a named argument set from args.json), or just a tool name with the verb in `with`."));

        // for an intent whose verb is a literal, `with` accepts exactly that verb's arguments
        var perIntent = new List<JsonNode>();
        foreach (var (toolName, sets) in cfg.Args)
        {
            if (!tools.TryGet(toolName, out var tool)) continue;
            foreach (var (intent, set) in sets)
            {
                var verb = ToolContext.NodeStr(set["verb"]);
                if (verb is null || verb.Contains("${") || tool.Spec.FirstOrDefault(v => v.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase)) is not { } spec) continue;
                perIntent.Add(O(("if", O(("properties", O(("run", O(("const", $"{toolName}/{intent}"))))), ("required", Arr("run")))),
                                ("then", O(("properties", O(("with", O(("type", "object"), ("properties", VerbProps(spec)), ("additionalProperties", false)))))))));
            }
        }

        var step = O(("type", "object"), ("required", Arr("run")), ("additionalProperties", false),
            ("properties", O(("run", run),
                ("with", O(("type", "object"), ("description", "Argument overrides merged over the named set."))),
                ("continueOnError", O(("type", Types("boolean", "string")), ("description", "Carry on with the next step if this one fails."))))),
            ("allOf", new JsonArray(perIntent.ToArray())));

        var workflow = O(("type", "object"), ("required", Arr("steps")), ("additionalProperties", false), ("properties", O(
            ("description", Str("Shown by `toolrunner workflows`.")),
            ("params", O(("type", "array"), ("items", O(("type", "string"))), ("description", "Positional parameter names; each becomes a variable."))),
            ("defaults", O(("type", "object"), ("additionalProperties", O(("type", "string"))), ("description", "Values for parameters not given on the command line."))),
            ("steps", O(("type", "array"), ("items", Ref("step")))))));

        return O(("$schema", Draft), ("title", "toolrunner workflows.json"), ("type", "object"),
                 ("properties", O(("$schema", Str()), ("workflows", O(("type", "object"), ("additionalProperties", Ref("workflow")))))),
                 ("definitions", O(("workflow", workflow), ("step", step))));
    }

    // ---- globals.json ----

    private static JsonObject GlobalsSchema()
    {
        JsonObject Scalar() => O(("type", Types("string", "number", "boolean", "object")));     // new node each time: a node has one parent
        JsonObject Opt(string doc) => O(("type", Types("string", "number", "boolean")), ("description", doc));
        var service = O(("type", "object"), ("description", "One service. These keys are conventions used by args.json; add your own."), ("properties", O(
            ("image", Opt("Image name:tag.")), ("src", Opt("Local source directory (git working tree).")),
            ("verify", O(("anyOf", Arr(O(("enum", Arr("none", "label", "tag"))), O(("pattern", Expr)))), ("description", "source/verify strategy."))),
            ("hostKey", Opt("Env-file key holding the host the consumer connects to.")), ("portKey", Opt("Env-file key holding the port.")),
            ("debugHost", Opt("Host to point the consumer at while debugging.")), ("debugPort", Opt("Port of the debug session.")),
            ("consumer", Opt("Compose service that connects to this one.")), ("logProbe", Opt("Log text that proves the consumer connected.")))),
            ("additionalProperties", Scalar()));
        return O(("$schema", Draft), ("title", "toolrunner globals*.json"), ("type", "object"), ("properties", O(
            ("$schema", Str()), ("$comment", Str()),
            ("APP_ROOT", Opt("Root of the application; on a server, an absolute POSIX path.")),
            ("DEPLOY_ROOT", Opt("The deployment folder the UI scans for stacks (default: APP_ROOT).")),
            ("REFERENCE_ROOT", Opt("A known-good copy of the deployment folder that `compare` checks against.")),
            ("COMPOSE_FILE", Opt("Compose file the compose verbs use by default.")),
            ("ENV_FILE", Opt("Env file the compose verbs use by default.")),
            ("COMPOSE_PROJECT", Opt("Compose project name (-p).")),
            ("WORK_DIR", Opt("Where generated files go; must stay gitignored.")),
            ("COMPOSE_DEBUG", Opt("Generated compose file.")), ("ENV_DEBUG", Opt("Generated env file.")),
            ("COMPOSE_DEBUG_IMAGE", Opt("Generated compose override file naming the services that run a debug image.")),
            ("REGISTRY_API", Opt("Registry v2 API base used to find base images; Artifactory: https://HOST/artifactory/api/docker/REPO-KEY.")),
            ("REGISTRY_PREFIX", Opt("What precedes a repository name in an image reference pulled from that registry, e.g. HOST/REPO-KEY.")),
            ("REGISTRY_USER", Opt("Registry user (basic auth). Keep it in globals.user.json.")),
            ("REGISTRY_TOKEN", Opt("Registry password or token (Bearer when there is no user). Keep it in globals.user.json.")),
            ("AZDO_URL", Opt(PipelineSettings.Spec.First(p => p.Var == "AZDO_URL").Help)),
            ("AZDO_PROJECT", Opt(PipelineSettings.Spec.First(p => p.Var == "AZDO_PROJECT").Help)),
            ("AZDO_PAT", Opt(PipelineSettings.Spec.First(p => p.Var == "AZDO_PAT").Help)),
            ("JFROG_URL", Opt(PipelineSettings.Spec.First(p => p.Var == "JFROG_URL").Help)),
            ("JFROG_USER", Opt(PipelineSettings.Spec.First(p => p.Var == "JFROG_USER").Help)),
            ("JFROG_TOKEN", Opt(PipelineSettings.Spec.First(p => p.Var == "JFROG_TOKEN").Help)),
            ("PIPELINE_POOL", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_POOL").Help)),
            ("PIPELINE_PLUGINS_REPO", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_PLUGINS_REPO").Help)),
            ("PIPELINE_CONFIG_REPO", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_CONFIG_REPO").Help)),
            ("PIPELINE_TESTS_REPO", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_TESTS_REPO").Help)),
            ("PIPELINE_MAIN_BRANCH", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_MAIN_BRANCH").Help)),
            ("PIPELINE_TAG_PREFIX", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_TAG_PREFIX").Help)),
            ("PIPELINE_PACKAGES", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_PACKAGES").Help)),
            ("PIPELINE_CREDENTIALS_GROUP", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_CREDENTIALS_GROUP").Help)),
            ("PIPELINE_NUGET_REPO_DEV", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_NUGET_REPO_DEV").Help)),
            ("PIPELINE_NUGET_REPO_PROD", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_NUGET_REPO_PROD").Help)),
            ("PIPELINE_DOCKER_REGISTRY_DEV", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_DOCKER_REGISTRY_DEV").Help)),
            ("PIPELINE_DOCKER_REGISTRY_PROD", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_DOCKER_REGISTRY_PROD").Help)),
            ("PIPELINE_DOCKER_REPO_DEV", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_DOCKER_REPO_DEV").Help)),
            ("PIPELINE_DOCKER_REPO_PROD", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_DOCKER_REPO_PROD").Help)),
            ("PIPELINE_PRODUCT", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_PRODUCT").Help)),
            ("PIPELINE_COMPOSE_FILE", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_COMPOSE_FILE").Help)),
            ("PIPELINE_ENV_FILE", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_ENV_FILE").Help)),
            ("PIPELINE_PLUGINS_DIR", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_PLUGINS_DIR").Help)),
            ("PIPELINE_BAKE_SERVICES", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_BAKE_SERVICES").Help)),
            ("PIPELINE_HEALTH_URL", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_HEALTH_URL").Help)),
            ("PIPELINE_TESTS_PROJECTS", Opt(PipelineSettings.Spec.First(p => p.Var == "PIPELINE_TESTS_PROJECTS").Help)),
            ("BUILD_PRODUCT_JSON", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_PRODUCT_JSON").Help)),
            ("BUILD_ENTITIES_PATH", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_ENTITIES_PATH").Help)),
            ("BUILD_TITLE", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_TITLE").Help)),
            ("BUILD_REALM", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_REALM").Help)),
            ("BUILD_VERSION", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_VERSION").Help)),
            ("BUILD_REGISTRY", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_REGISTRY").Help)),
            ("BUILD_NUGET_REPO", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_NUGET_REPO").Help)),
            ("BUILD_TEMPLATE_DIR", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_TEMPLATE_DIR").Help)),
            ("BUILD_OUTPUT_DIR", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_OUTPUT_DIR").Help)),
            ("BUILD_HOST", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_HOST").Help)),
            ("BUILD_PORT_BASE", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_PORT_BASE").Help)),
            ("BUILD_VALUES", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_VALUES").Help)),
            ("BUILD_PRIVILEGE_FORMAT", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_PRIVILEGE_FORMAT").Help)),
            ("BUILD_PRIVILEGE_SEPARATOR", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_PRIVILEGE_SEPARATOR").Help)),
            ("BUILD_EXPECT", Opt(BuildSettings.Spec.First(p => p.Var == "BUILD_EXPECT").Help)),
            ("CONTAINER_ENGINE", Opt("The container CLI used wherever a tool runs docker: docker (default), podman, or a full path.")),
            ("VERSION", Opt("The version builds and checkouts use. Unset: the version detected from the stack's image tags.")),
            ("VERSION_PATTERN", Opt("Regex that finds the version inside an image tag.")),
            ("VERSION_IGNORE", Opt("Regex for the parts of an image tag that describe the base image (alpine3.20 ...); never read as the application version.")),
            ("VERSION_GIT_REFS", Opt("`;`-separated git ref templates tried in order for a version; {version} is replaced.")),
            ("VERSION_LATEST_REFS", Opt("`;`-separated git refs that the version `latest` means.")),
            ("VSDBG_DIR", Opt("Folder holding an unpacked vsdbg that debug images copy in (must match the image's libc: linux-x64 or linux-musl-x64).")),
            ("VSDBG_URL", Opt("GetVsDbg.sh location used when VSDBG_DIR is not set (default https://aka.ms/getvsdbgsh).")),
            ("ssh", O(("type", "object"), ("description", "Used with --host: connect to a Linux server over ssh (key auth only)."), ("properties", O(
                ("target", Opt("user@host.")), ("port", Opt("ssh port.")), ("identity", Opt("Path to the private key.")),
                ("options", Opt("`;`-separated ssh -o options.")))), ("additionalProperties", false))),
            ("services", O(("type", "object"), ("additionalProperties", service))))),
            ("additionalProperties", Scalar()));
    }

    // ---- output ----

    public static IReadOnlyDictionary<string, string> Generate(ToolRegistry tools, ConfigSet cfg) => new Dictionary<string, string>
    {
        ["globals.schema.json"] = Pretty(GlobalsSchema()),
        ["args.schema.json"] = Pretty(ArgsSchema(tools)),
        ["workflows.schema.json"] = Pretty(WorkflowsSchema(tools, cfg))
    };

    private static string Pretty(JsonNode n) => n.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    }) + "\n";

    /// Writes <config dir>/schemas/*.schema.json, touching only files whose content changed (so a file watcher
    /// does not fire needlessly). Returns the names written.
    public static List<string> Write(string configDir, ToolRegistry tools, ConfigSet cfg)
    {
        var dir = Path.Combine(configDir, "schemas");
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (var (name, text) in Generate(tools, cfg))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && File.ReadAllText(path) == text) continue;
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            written.Add(name);
        }
        return written;
    }
}
