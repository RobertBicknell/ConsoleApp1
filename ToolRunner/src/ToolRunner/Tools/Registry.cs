using ToolRunner.Core;

namespace ToolRunner.Tools;

/// The tools toolrunner ships with; shared by the CLI and the UI so both see the same set.
public static class Registry
{
    public static ToolRegistry Default() =>
        new(new GitTool(), new FileTool(), new ComposeTool(), new DockerTool(), new LogFileTool(), new SourceTool(), new PluginsTool(), new DebugImageTool(), new VersionTool(), new DotnetTool(), new ShellTool());
}
