using System.Text;

namespace Csv2Xml.Runtime.Csv;

/// <summary>The CSV text is malformed (e.g. a quoted field never closed). Line is the 1-based physical line where the bad record starts.</summary>
public sealed class CsvFormatException(long line, string message) : FormatException($"line {line}: {message}")
{
    public long Line { get; } = line;
}

/// <summary>
/// Streaming RFC 4180 parser: quotes, escaped quotes, embedded delimiters/newlines, CR/LF/CRLF, one row in memory at a time.
/// A leading byte-order mark (U+FEFF) is dropped. Empty lines are skipped. An unterminated quoted field throws CsvFormatException.
/// </summary>
public static class CsvParser
{
    static CsvParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-125x, iso-8859-x, ...

    public static IEnumerable<string[]> Read(TextReader reader, char delimiter = ',')
    {
        var buf = new char[8192];
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false, pendingQuote = false, fieldStarted = false, skipLf = false, first = true;
        long line = 1, recordLine = 1;
        int n;

        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (first) { first = false; if (c == '﻿') continue; }
                if (c == '\n' && !skipLf || c == '\r') line++;

                if (skipLf) { skipLf = false; if (c == '\n') continue; }

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (c == '"') { field.Append('"'); continue; }
                    inQuotes = false; // closing quote; reprocess c as unquoted
                }

                if (inQuotes)
                {
                    if (c == '"') pendingQuote = true; else field.Append(c);
                    continue;
                }

                if (c == '"' && !fieldStarted) { inQuotes = true; fieldStarted = true; continue; }

                if (c == delimiter)
                {
                    row.Add(field.ToString()); field.Clear(); fieldStarted = false;
                }
                else if (c == '\n' || c == '\r')
                {
                    skipLf = c == '\r';
                    if (row.Count > 0 || fieldStarted || field.Length > 0)
                    {
                        row.Add(field.ToString()); field.Clear(); fieldStarted = false;
                        yield return row.ToArray();
                        row.Clear();
                    }
                    recordLine = line;
                }
                else { field.Append(c); fieldStarted = true; }
            }
        }

        if (inQuotes && !pendingQuote) throw new CsvFormatException(recordLine, "quoted field is never closed");
        if (row.Count > 0 || fieldStarted || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }

    /// <summary>
    /// Opens a CSV file for reading with strict decoding: bytes invalid in the encoding throw DecoderFallbackException instead of
    /// silently becoming U+FFFD. Encoding: a byte-order mark (UTF-8/16/32) wins; otherwise the named encoding; default UTF-8.
    /// </summary>
    public static StreamReader Open(string path, string? encoding = null) =>
        new(path, Strict(encoding), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

    /// <summary>Same as Open, over a stream (e.g. an upload).</summary>
    public static StreamReader Open(Stream stream, string? encoding = null) =>
        new(stream, Strict(encoding), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);

    /// <summary>User-facing message for malformed CSV or undecodable input; null for any other exception.</summary>
    public static string? DescribeInputError(Exception e) => e switch
    {
        CsvFormatException f => f.Message,
        DecoderFallbackException => "input contains bytes that are not valid in the declared encoding (UTF-8 unless the mapping says otherwise); "
            + "re-save the file as UTF-8 or set the mapping's Encoding, e.g. \"windows-1252\"",
        _ => null,
    };

    static Encoding Strict(string? name) =>
        name is null || name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || name.Equals("utf8", StringComparison.OrdinalIgnoreCase)
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            : Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}
