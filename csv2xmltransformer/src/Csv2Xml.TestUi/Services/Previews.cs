using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Csv2Xml.Runtime.Csv;

namespace Csv2Xml.TestUi.Services;

/// <summary>What a preview window shows. Format: xml | json | csv | text. Rows is set (and Text empty) for csv.</summary>
public sealed record Preview(string Title, string Format, string Text, IReadOnlyList<string[]>? Rows = null, string? Note = null);

/// <summary>
/// Content waiting to be shown in a preview window, addressed by an unguessable id. The preview endpoint serves only what the
/// UI put here, so no URL can read an arbitrary file. Holds the most recent <see cref="Capacity"/> entries.
/// </summary>
public sealed class PreviewStore
{
    const int Capacity = 100;
    const int MaxCsvRows = 5000;
    readonly ConcurrentDictionary<Guid, Preview> items = new();
    readonly ConcurrentQueue<Guid> order = new();

    public Guid Add(Preview p)
    {
        var id = Guid.NewGuid();
        items[id] = p;
        order.Enqueue(id);
        while (order.Count > Capacity && order.TryDequeue(out var old)) items.TryRemove(old, out _);
        return id;
    }

    public Preview? Get(Guid id) => items.GetValueOrDefault(id);

    /// <summary>A file as the tool sees it: CSV parsed with the same parser, delimiter and strict encoding the runtime uses.</summary>
    public static Preview FromFile(string path, char delimiter = ',', string? encoding = null)
    {
        var title = Path.GetFileName(path);
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".csv" or ".tsv" or ".txt" when delimiter != '\0' => Csv(path, delimiter, encoding),
                ".xml" or ".xsd" => new Preview(title, "xml", File.ReadAllText(path)),
                ".json" => new Preview(title, "json", File.ReadAllText(path)),
                _ => new Preview(title, "text", File.ReadAllText(path)),
            };
        }
        catch (Exception e) { return new Preview(title, "text", "", Note: CsvParser.DescribeInputError(e) ?? e.Message); }
    }

    static Preview Csv(string path, char delimiter, string? encoding)
    {
        using var r = CsvParser.Open(path, encoding);
        var rows = CsvParser.Read(r, delimiter).Take(MaxCsvRows + 1).ToList();
        var note = rows.Count > MaxCsvRows ? $"showing the first {MaxCsvRows} rows" : null;
        return new Preview(Path.GetFileName(path), "csv", "", rows.Take(MaxCsvRows).ToList(),
            note is null ? $"delimiter '{delimiter}', encoding {encoding ?? "utf-8"}" : $"{note}; delimiter '{delimiter}', encoding {encoding ?? "utf-8"}");
    }

    /// <summary>The preview page: a shell that preview.js renders from the embedded JSON (System.Text.Json escapes &lt; so it cannot close the script tag).</summary>
    public static string Page(Preview p) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1.0" />
        <title>{{WebUtility.HtmlEncode(p.Title)}} — preview</title>
        <script>try{document.documentElement.dataset.theme=localStorage.getItem('c2x-theme')||'dark'}catch{document.documentElement.dataset.theme='dark'}</script>
        <link rel="stylesheet" href="/app.css" /><link rel="stylesheet" href="/preview.css" />
        </head><body class="preview">
        <script id="data" type="application/json">{{JsonSerializer.Serialize(p, JsonSerializerOptions.Web)}}</script>
        <div id="root"></div>
        <script src="/preview.js"></script>
        </body></html>
        """;
}
