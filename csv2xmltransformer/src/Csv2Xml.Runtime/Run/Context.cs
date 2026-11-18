namespace Csv2Xml.Runtime.Run;

/// <summary>An entry destined for the output zip. Content is opened lazily so large files are never held in memory.</summary>
public sealed class PackageFile(string name, Func<Stream> open)
{
    public string Name { get; set; } = name;
    public Func<Stream> Open { get; set; } = open;

    public static PackageFile FromBytes(string name, byte[] bytes) => new(name, () => new MemoryStream(bytes, writable: false));
    public static PackageFile FromText(string name, string text) => FromBytes(name, new System.Text.UTF8Encoding(false).GetBytes(text));
    public static PackageFile FromFile(string name, string path) => new(name, () => File.OpenRead(path));
}

/// <summary>
/// What middleware (a plugin's public Transform(Context)) sees and may change just before packaging:
/// edit ReportText or Report, add/remove/replace Files. Whatever is here afterwards is zipped and sent.
/// </summary>
public sealed class Context
{
    public required JobSpec Job { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public required RunReport Report { get; init; }
    public required string ReportText { get; set; }
    public required IList<PackageFile> Files { get; init; }
}
