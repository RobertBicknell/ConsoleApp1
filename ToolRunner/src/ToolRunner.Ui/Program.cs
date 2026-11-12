using ToolRunner.Core;
using ToolRunner.Ui;
using ToolRunner.Ui.Components;

// Local-only viewer for toolrunner config, state and runs. Config dir: TOOLRUNNER_CONFIG, else ./config.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();      // find framework scripts when run from bin\ in any environment, not only Development
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(_ => new LiveConfig(ConfigLoader.ResolveDir(null)));
builder.Services.AddSingleton<Workspace>();
builder.Services.AddSingleton<RunSession>();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();      // serves _framework/blazor.web.js; without it the page renders but nothing is clickable
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// A file exactly as it is, for the browser's own rendering (XML tree, JSON viewer). Read through the selected host,
// so it works over ssh too. Never served as HTML, whatever it contains.
// The documentation, generated on request so its reference tables always match this build and this config.
app.MapGet("/docs", (LiveConfig live) =>
{
    var template = Path.Combine(live.Dir, "docs.template.html");
    return File.Exists(template)
        ? Results.Content(Docs.Generate(File.ReadAllText(template), ToolRunner.Tools.Registry.Default(), live.Cfg), "text/html; charset=utf-8")
        : Results.NotFound($"no docs.template.html in {live.Dir}");
});

app.MapGet("/raw", async (string path, LiveConfig live, CancellationToken ct) =>
{
    var text = await live.CreateHost().ReadTextAsync(path, ct);
    if (text is null) return Results.NotFound($"not found: {path}");
    var type = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".xml" or ".config" or ".csproj" or ".props" or ".targets" or ".xaml" or ".nuspec" or ".resx" => "application/xml",
        _ => "text/plain"
    };
    return Results.Text(text, type, System.Text.Encoding.UTF8);
});
app.Run();
