using System.Text.RegularExpressions;

namespace SamplePlugin;

[Args(new[] { "Text", "Pattern" })]
[Description("Finds the first match of a regular expression in a text.")]
[ReadOnly]
public sealed class ExtractText : IDotnetTool
{
    public Task<Dictionary<string, string>> ExecAsync(string[] args)
    {
        var m = Regex.Match(args[0], args[1]);
        return Task.FromResult(new Dictionary<string, string> { ["EXTRACTED"] = m.Value, ["EXTRACTED_FOUND"] = m.Success ? "true" : "false" });
    }
}

[Args(new[] { "InputFilePath", "OutputFilePath" })]
[Description("Copies a text file, upper-casing it. An example of a command that writes.")]
public sealed class UpperCaseFile : IDotnetTool
{
    public async Task<Dictionary<string, string>> ExecAsync(string[] args)
    {
        var text = await File.ReadAllTextAsync(args[0]);       // a missing file throws: the step fails with the message
        await File.WriteAllTextAsync(args[1], text.ToUpperInvariant());
        return new() { ["OUTPUT_FILE"] = args[1], ["OUTPUT_CHARS"] = text.Length.ToString() };
    }
}
