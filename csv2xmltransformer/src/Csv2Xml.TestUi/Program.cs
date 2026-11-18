using Csv2Xml.TestUi.Components;
using Csv2Xml.TestUi.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<PreviewStore>();
builder.Services.AddSingleton<UiSettings>();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
// Serves only content the UI registered in the store (see PreviewStore), never a path from the URL.
app.MapGet("/preview/{id:guid}", (Guid id, PreviewStore store) =>
    store.Get(id) is { } p ? Results.Content(PreviewStore.Page(p), "text/html; charset=utf-8") : Results.NotFound("preview expired; open it again from the UI"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
