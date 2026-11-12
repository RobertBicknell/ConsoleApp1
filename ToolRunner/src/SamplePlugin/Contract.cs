namespace SamplePlugin;

// The whole contract. toolrunner matches these three types BY NAME, so copy this file into your own assembly
// (any namespace); nothing references toolrunner.

/// A command. The class name is the command name. Throw to fail the step.
public interface IDotnetTool
{
    /// args arrive in the order [Args] lists them. Every returned pair becomes a variable for the following steps.
    Task<Dictionary<string, string>> ExecAsync(string[] args);
}

/// The names of the arguments, in the order ExecAsync reads them.
[AttributeUsage(AttributeTargets.Class)]
public sealed class ArgsAttribute(params string[] names) : Attribute
{
    public string[] Names { get; } = names;
}

/// Optional: help text shown beside the command.
[AttributeUsage(AttributeTargets.Class)]
public sealed class DescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// Optional: the command changes nothing, so it may run while the read-only switch is on.
[AttributeUsage(AttributeTargets.Class)]
public sealed class ReadOnlyAttribute : Attribute;
