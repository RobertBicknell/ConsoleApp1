using System.Xml;
using System.Xml.Schema;

namespace Csv2Xml.Runtime.Validation;

/// <summary>Checks a value against an XSD built-in type name as stored in specs (e.g. "int", "datetime").</summary>
public static class TypeCheck
{
    /// <returns>null when valid or the type is not a recognised built-in; otherwise a message.</returns>
    public static string? Error(string dataType, string value)
    {
        if (!Enum.TryParse<XmlTypeCode>(dataType, ignoreCase: true, out var code)) return null;
        var dt = XmlSchemaSimpleType.GetBuiltInSimpleType(code)?.Datatype;
        if (dt is null) return null;
        try { dt.ParseValue(value, new NameTable(), null); return null; }
        catch (Exception e) when (e is XmlSchemaException or FormatException or OverflowException)
        {
            return $"'{value}' is not a valid {dataType}";
        }
    }
}
