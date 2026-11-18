using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Runtime.Run;

/// <summary>The settled definition of a run: one mapping per kind, the run-parameter definitions, and packaging policy.</summary>
/// <param name="PackageName">Zip file name; tokens {Name} and {timestamp}.</param>
/// <param name="OmitXmlDeclaration">When true (default) the XML files carry no <c>&lt;?xml version encoding?&gt;</c> declaration; they are UTF-8 without a BOM either way.</param>
public sealed record JobSpec(
    string Name,
    IReadOnlyList<RunParameterDef> Parameters,
    IReadOnlyList<MappingSpec> Mappings,
    string PackageName = "{Name}_{timestamp}.zip",
    bool ValidateOutput = true,
    ErrorPolicy ErrorPolicy = ErrorPolicy.Halt,
    bool OmitXmlDeclaration = true);

/// <summary>What is supplied when a run starts: kind -> CSV path, and parameter key -> value.</summary>
/// <param name="XsdText">Optional XSD content; when given and the job validates output, every XML is validated against it before sending.</param>
public sealed record RunRequest(
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string? XsdText = null);
