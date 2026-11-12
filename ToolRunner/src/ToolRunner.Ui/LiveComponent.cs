using Microsoft.AspNetCore.Components;

namespace ToolRunner.Ui;

/// Base for every tab: re-renders when the config files, the workspace (catalog, tool outputs) or a run changes.
public abstract class LiveComponent : ComponentBase, IDisposable
{
    [Inject] protected LiveConfig Live { get; set; } = default!;
    [Inject] protected Workspace Ws { get; set; } = default!;
    [Inject] protected RunSession Session { get; set; } = default!;

    protected override void OnInitialized()
    {
        Live.Changed += Refresh; Ws.Changed += Refresh; Session.Changed += Refresh;
    }

    private void Refresh() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Live.Changed -= Refresh; Ws.Changed -= Refresh; Session.Changed -= Refresh;
        GC.SuppressFinalize(this);
    }

    protected static string Rel(string root, string path) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? (path[root.Length..].TrimStart('/') is { Length: > 0 } r ? r : ".") : path;
}
