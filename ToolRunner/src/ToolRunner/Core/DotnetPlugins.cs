using System.Reflection;
using System.Runtime.Loader;

namespace ToolRunner.Core;

/// A command found in a plugin assembly: a public class that implements an interface NAMED IDotnetTool with
///   Task<Dictionary<string, string>> ExecAsync(string[] args)
/// The class name is the command name. An attribute NAMED Args lists the arguments in the order ExecAsync expects them.
/// Optional, also matched by name only: [Description("...")] for the help text, [ReadOnly] for a command that changes nothing.
/// Matching is by name, so a plugin declares these three types itself and never references toolrunner.
public sealed record DotnetCommand(string Name, IReadOnlyList<string> Args, string Doc, bool ReadOnly, string Assembly, Type Type, MethodInfo Exec);

public sealed record DotnetPluginSet(string Dir, IReadOnlyList<DotnetCommand> Commands, IReadOnlyList<string> Notes);

public static class DotnetPlugins
{
    /// Where plugin binaries are dropped: %TOOLRUNNER_PLUGINS%, else the `plugins` folder beside ToolRunner.sln (found by
    /// walking up from the program), else `plugins` beside the program.
    public static string Dir { get; set; } = DefaultDir();

    private static string DefaultDir()
    {
        if (Environment.GetEnvironmentVariable("TOOLRUNNER_PLUGINS") is { Length: > 0 } env) return env;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ToolRunner.sln"))) return Path.Combine(d.FullName, "plugins");
        return Path.Combine(AppContext.BaseDirectory, "plugins");
    }

    private static readonly object Gate = new();
    private static (string Stamp, DotnetPluginSet Set)? _cache;

    /// The commands in the plugins folder now. Reloaded whenever a file there changes; assemblies are read into memory,
    /// so the files are never locked and a post-build copy can overwrite them while toolrunner runs.
    public static DotnetPluginSet Load()
    {
        var dir = Dir;
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dll").Order(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        var stamp = dir + "|" + string.Join("|", files.Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}:{new FileInfo(f).Length}"));
        lock (Gate)
        {
            if (_cache is { } c && c.Stamp == stamp) return c.Set;
            var set = Read(dir, files);
            _cache = (stamp, set);
            return set;
        }
    }

    private static DotnetPluginSet Read(string dir, List<string> files)
    {
        var commands = new List<DotnetCommand>();
        var notes = new List<string>();
        var context = new AssemblyLoadContext("toolrunner-plugins-" + Guid.NewGuid().ToString("N")[..6]);
        Assembly? FromFile(string path)
        {
            using var dll = new MemoryStream(File.ReadAllBytes(path));
            var pdb = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdb)) return context.LoadFromStream(dll);
            using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
            return context.LoadFromStream(dll, symbols);
        }
        var loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        // a plugin's own dependencies are taken from the same folder
        context.Resolving += (_, name) => name.Name is { } n && loaded.TryGetValue(n, out var a) ? a
            : name.Name is { } m && File.Exists(Path.Combine(dir, m + ".dll")) ? loaded[m] = FromFile(Path.Combine(dir, m + ".dll"))! : null;

        foreach (var file in files)
        {
            var simple = Path.GetFileNameWithoutExtension(file);
            try
            {
                if (!loaded.TryGetValue(simple, out var asm)) loaded[simple] = asm = FromFile(file)!;
                foreach (var type in asm.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
                {
                    if (type.GetInterfaces().FirstOrDefault(i => i.Name == "IDotnetTool") is not { } contract) continue;
                    var exec = contract.GetMethod("ExecAsync", [typeof(string[])]);
                    if (exec is null || !typeof(Task).IsAssignableFrom(exec.ReturnType)) { notes.Add($"{simple}: {type.Name} implements IDotnetTool but it has no Task ExecAsync(string[])"); continue; }
                    if (type.GetConstructor(Type.EmptyTypes) is null) { notes.Add($"{simple}: {type.Name} has no parameterless constructor"); continue; }
                    if (commands.FirstOrDefault(k => k.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase)) is { } clash) { notes.Add($"{simple}: {type.Name} ignored, {clash.Assembly} already has a command of that name"); continue; }

                    var attributes = type.GetCustomAttributesData();
                    CustomAttributeData? Attr(string name) => attributes.FirstOrDefault(a => a.AttributeType.Name == name + "Attribute" || a.AttributeType.Name == name);
                    var args = Attr("Args")?.ConstructorArguments.SelectMany(a => a.Value is IEnumerable<CustomAttributeTypedArgument> many
                        ? many.Select(x => x.Value?.ToString()) : [a.Value?.ToString()]).OfType<string>().Where(s => s.Length > 0).ToList() ?? new();
                    var doc = Attr("Description")?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";
                    commands.Add(new DotnetCommand(type.Name, args, doc, Attr("ReadOnly") is not null, simple, type, exec));
                }
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or ReflectionTypeLoadException or NotSupportedException)
            { notes.Add($"{simple}: not loaded ({e.GetType().Name}: {e.Message})"); }
        }
        return new DotnetPluginSet(dir, commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(), notes);
    }

    /// Runs a command with its arguments in declared order. Whatever it returns becomes variables.
    public static async Task<Dictionary<string, string>> RunAsync(DotnetCommand command, string[] args)
    {
        try
        {
            var task = (Task)command.Exec.Invoke(Activator.CreateInstance(command.Type), [args])!;
            await task;
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return result is IEnumerable<KeyValuePair<string, string>> pairs ? pairs.Where(p => p.Key.Length > 0).ToDictionary(p => p.Key, p => p.Value ?? "") : new();
        }
        catch (Exception e) when (e is not OperationCanceledException)      // whatever the user's code throws is a failed step, not a crash
        {
            var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            throw new InvalidOperationException($"{command.Name} threw {cause.GetType().Name}: {cause.Message}", cause);
        }
    }
}
