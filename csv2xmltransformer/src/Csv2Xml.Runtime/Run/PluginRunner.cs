using System.Reflection;

namespace Csv2Xml.Runtime.Run;

/// <summary>Finds the first public Transform(Context) in the first assembly in a plugin folder and runs it.</summary>
public static class PluginRunner
{
    public sealed record Handle(string Name, Action<Context> Invoke);

    public static Handle? Find(string? folder)
    {
        if (folder is null || !Directory.Exists(folder)) return null;
        var self = typeof(Context).Assembly.GetName().Name + ".dll";

        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                     .Where(f => !string.Equals(Path.GetFileName(f), self, StringComparison.OrdinalIgnoreCase)))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(dll); } catch (BadImageFormatException) { continue; }

            foreach (var type in asm.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } || t is { IsAbstract: true, IsSealed: true }))
            {
                var m = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.Name == "Transform" && x.GetParameters() is [{ } p] && p.ParameterType == typeof(Context));
                if (m is null) continue;
                return new Handle($"{type.FullName}.Transform ({Path.GetFileName(dll)})", ctx =>
                {
                    try
                    {
                        var target = m.IsStatic ? null : Activator.CreateInstance(type);
                        m.Invoke(target, [ctx]);
                    }
                    catch (TargetInvocationException e) { throw new RunException([$"plugin failed: {e.InnerException?.Message ?? e.Message}"]); }
                });
            }
            return null; // only the first assembly is considered
        }
        return null;
    }
}
