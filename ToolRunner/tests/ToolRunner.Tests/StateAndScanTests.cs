using ToolRunner.Core;

namespace ToolRunner.Tests;

public class StateTests
{
    private static RunState With(params (string K, string V)[] vars)
    {
        var s = new RunState();
        foreach (var (k, v) in vars) s.Vars[k] = v;
        return s;
    }

    [Fact]
    public void Names_can_be_computed_from_other_variables()
    {
        var s = With(("service", "pluginhost"), ("services.pluginhost.image", "myorg/pluginhost:dev"));
        Assert.Equal("myorg/pluginhost:dev", s.Expand("${services.${service}.image}"));
    }

    [Fact]
    public void Values_can_reference_values_and_a_default_covers_an_undefined_name()
    {
        var s = With(("APP_ROOT", "/srv/app"), ("ENV_FILE", "${APP_ROOT}/.env"));
        Assert.Equal("/srv/app/.env", s.Expand("${ENV_FILE}"));
        Assert.Equal("fallback", s.Expand("${NOPE:-fallback}"));
    }

    [Fact]
    public void An_undefined_variable_without_a_default_is_an_error_naming_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new RunState().Expand("${MISSING}"));
        Assert.Contains("MISSING", ex.Message);
    }

    [Fact]
    public void A_reference_cycle_fails_instead_of_looping()
    {
        var s = With(("a", "${b}"), ("b", "${a}"));
        Assert.Throws<InvalidOperationException>(() => s.Expand("${a}"));
    }

    [Fact]
    public void Names_are_case_insensitive()
    {
        Assert.Equal("v", With(("Key", "v")).Expand("${KEY}"));
    }

    [Fact]
    public void Dollar_keys_in_globals_are_editor_metadata_not_variables()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolrunner-state-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "globals.json"), """{ "$schema": "s", "A": "1", "n": { "$comment": "c", "b": "2" } }""");
            var vars = ConfigLoader.LoadState(dir, null, []).Vars;
            Assert.Equal(["A", "n.b"], vars.Keys.Order().ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Layers_apply_in_order_globals_then_user_then_host_then_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "toolrunner-layers-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "globals.json"), """{ "X": "globals", "Y": "globals", "Z": "globals", "W": "globals" }""");
            File.WriteAllText(Path.Combine(dir, "globals.user.json"), """{ "X": "user", "Y": "user", "Z": "user" }""");
            File.WriteAllText(Path.Combine(dir, "globals.prod.json"), """{ "X": "host", "Y": "host" }""");
            var v = ConfigLoader.LoadState(dir, "prod", ["X=set"]).Vars;
            Assert.Equal(("set", "host", "user", "globals"), (v["X"], v["Y"], v["Z"], v["W"]));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class ScanTests
{
    private const string Compose = """
        services:
          web:
            image: x:${TAG:-1.0}
            env_file: web.env
            environment:
              - A=${A}
              - B
              C: ${C:-cee}
          db:
            env_file:
              - db.env
            environment:
              DB_PASSWORD: ${DB_PASSWORD}
        # ${COMMENTED}
        """;

    [Fact]
    public void Compose_parser_finds_interpolations_defaults_environment_keys_and_env_files()
    {
        var (refs, envFiles) = Scanner.Compose("c.yml", Compose);

        Assert.Contains(refs, r => r is { Name: "TAG", Kind: "interpolation-default", Value: "1.0", Service: "web" });
        Assert.Contains(refs, r => r is { Name: "A", Kind: "interpolation" });
        Assert.Contains(refs, r => r is { Name: "A", Kind: "environment", Service: "web" });
        Assert.Contains(refs, r => r is { Name: "B", Kind: "environment", Value: null });
        Assert.Contains(refs, r => r is { Name: "C", Kind: "interpolation-default", Value: "cee" });
        Assert.DoesNotContain(refs, r => r.Name == "COMMENTED");
        Assert.Equal([("web.env", "web"), ("db.env", "db")], envFiles.Select(e => (e.Path, e.Service)).ToArray());
    }

    [Fact]
    public void Status_says_whether_each_variable_is_defined_defaulted_or_missing()
    {
        var (refs, _) = Scanner.Compose("c.yml", Compose);
        refs.AddRange(Scanner.EnvFile(".env", "A=1\nUNUSED=z\n", "env-file"));

        var rows = Scanner.Merge(refs).ToDictionary(r => r.Name);

        Assert.Equal("ok", rows["A"].Status);                       // used and defined
        Assert.Equal("default", rows["TAG"].Status);                // used, not defined, has a default
        Assert.Equal("undefined", rows["DB_PASSWORD"].Status);      // used, not defined, no default
        Assert.Equal("unused", rows["UNUSED"].Status);              // defined, never referenced
        Assert.Equal("compose-literal", rows["B"].Status);
    }

    [Fact]
    public void Secret_looking_values_are_reported_as_they_are()
    {
        var (refs, _) = Scanner.Compose("c.yml", Compose);
        refs.AddRange(Scanner.EnvFile(".env", "DB_PASSWORD=hunter2\n", "env-file"));

        var row = Scanner.Merge(refs).Single(r => r.Name == "DB_PASSWORD");

        Assert.Equal("hunter2", row.Value);       // shown on screen; only the spool file hides it
        Assert.Equal("ok", row.Status);
    }

    [Fact]
    public void A_variable_that_only_forwards_itself_has_no_value_of_its_own()
    {
        var (refs, _) = Scanner.Compose("c.yml", Compose);
        Assert.Null(Scanner.Merge(refs).Single(r => r.Name == "DB_PASSWORD").Value);
    }

    [Fact]
    public void Env_file_parser_skips_comments_and_strips_quotes_and_export()
    {
        var refs = Scanner.EnvFile(".env", "# A=1\nexport B=\"two\"\nC='3'\nnot a pair\n", "env-file");
        Assert.Equal([("B", "two"), ("C", "3")], refs.Select(r => (r.Name, r.Value!)).ToArray());
    }
}
