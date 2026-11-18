using System.Text.Json;

namespace Csv2Xml.TestUi.Services;

/// <summary>
/// Last-used paths per field (xsd, csv, config, ...), kept in %LOCALAPPDATA%\Csv2Xml\ui-settings.json so pickers reopen where
/// the user last was. Missing or unreadable settings fall back to <see cref="Defaults"/>; a failed save is ignored (convenience only).
/// </summary>
public sealed class UiSettings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Csv2Xml", "ui-settings.json");
    readonly Lock gate = new();
    readonly Dictionary<string, string> values = Load();

    public string Get(string key) { lock (gate) return values.GetValueOrDefault(key) ?? Defaults(key); }

    public void Set(string key, string value)
    {
        lock (gate)
        {
            if (values.GetValueOrDefault(key) == value) return;
            values[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The repo's samples folder when running from a checkout, else null.</summary>
    public static readonly string? SamplesDir = FindSamples();

    public static string WorkDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Csv2Xml");

    static string Defaults(string key) => key switch
    {
        "xsd" => SamplesDir is null ? WorkDir + Path.DirectorySeparatorChar : Path.Combine(SamplesDir, "dev.xsd"),
        "csv" => SamplesDir is null ? WorkDir + Path.DirectorySeparatorChar : Path.Combine(SamplesDir, "csv", "orders_duplicate_headers.csv"),
        "config" => Path.Combine(WorkDir, "mappings", "mapping.json"),
        "generated" => Path.Combine(WorkDir, "generated"),
        "runout" => Path.Combine(WorkDir, "output"),
        _ => "",
    };

    static Dictionary<string, string> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    static string? FindSamples()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "samples", "dev.xsd"))) return Path.Combine(d.FullName, "samples");
        return null;
    }
}
