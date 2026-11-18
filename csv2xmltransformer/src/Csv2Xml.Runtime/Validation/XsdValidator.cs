using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Schema;

namespace Csv2Xml.Runtime.Validation;

public sealed record ValidationIssue(int Line, int Column, XmlSeverityType Severity, string Message);

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, bool Truncated)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>XSD identity (content hash), compilation from text, and streamed (no DOM) conformance checking.</summary>
public static class XsdValidator
{
    /// <summary>
    /// Identity of an XSD: SHA-256 (hex) of its text as UTF-8, ignoring a leading BOM and CRLF/LF differences, so the same
    /// file read by different means or checked out on another OS hashes equal. Location-independent: stored mappings stay portable.
    /// </summary>
    public static string Hash(string xsdText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xsdText.TrimStart('﻿').Replace("\r\n", "\n"))));

    /// <summary>
    /// Compiles a single self-contained XSD from text. Throws XmlSchemaException / XmlException when invalid, including when it
    /// uses xs:include / xs:import / xs:redefine: text has no location to resolve them from, and ignoring them would silently
    /// drop types. Merge such schemas into one file first.
    /// </summary>
    public static XmlSchemaSet Compile(string xsdText)
    {
        var set = new XmlSchemaSet { XmlResolver = null };
        XmlSchema schema;
        using (var xr = XmlReader.Create(new StringReader(xsdText), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            schema = set.Add(null, xr)!;
        var external = schema.Includes.OfType<XmlSchemaExternal>().Select(x => x.SchemaLocation ?? (x as XmlSchemaImport)?.Namespace ?? "?").ToList();
        if (external.Count > 0)
            throw new XmlSchemaException($"xs:include/xs:import/xs:redefine is not supported ({string.Join(", ", external)}); supply a single self-contained XSD");
        set.Compile();
        return set;
    }

    public static ValidationReport Validate(XmlReader xml, XmlSchemaSet schemas, int maxIssues = 100)
    {
        var issues = new List<ValidationIssue>();
        var truncated = false;
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
            DtdProcessing = DtdProcessing.Prohibit,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
        };
        IXmlLineInfo? li = null;
        settings.ValidationEventHandler += (_, e) =>
        {
            if (issues.Count >= maxIssues) { truncated = true; return; }
            issues.Add(new ValidationIssue(e.Exception?.LineNumber ?? li?.LineNumber ?? 0, e.Exception?.LinePosition ?? li?.LinePosition ?? 0, e.Severity, e.Message));
        };
        using var reader = XmlReader.Create(xml, settings);
        li = (IXmlLineInfo)reader;
        try { while (reader.Read()) { if (truncated) break; } }
        catch (XmlException ex) { issues.Add(new ValidationIssue(ex.LineNumber, ex.LinePosition, XmlSeverityType.Error, "not well-formed: " + ex.Message)); }
        return new ValidationReport(issues, truncated);
    }

    public static ValidationReport Validate(string xml, XmlSchemaSet schemas, int maxIssues = 100)
    {
        using var r = XmlReader.Create(new StringReader(xml));
        return Validate(r, schemas, maxIssues);
    }

    public static ValidationReport ValidateFile(string xmlPath, XmlSchemaSet schemas, int maxIssues = 100)
    {
        using var fs = File.OpenRead(xmlPath);
        using var r = XmlReader.Create(fs);
        return Validate(r, schemas, maxIssues);
    }
}
