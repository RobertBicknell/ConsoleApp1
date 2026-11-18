using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Csv2Xml.Runtime.Run;

namespace Csv2Xml.Runtime.Mapping;

/// <summary>
/// Storage form of mappings and jobs: UTF-8 JSON inside a versioned envelope, so a stored config identifies itself and
/// future format changes can be detected rather than misread.
/// </summary>
public static class ConfigJson
{
    public const int Version = 1;
    const string MappingFormat = "csv2xml.mapping";
    const string JobFormat = "csv2xml.job";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        RespectNullableAnnotations = true,           // null for a non-nullable member is rejected
        RespectRequiredConstructorParameters = true, // a missing required member is rejected
    };

    sealed record Envelope<T>(string Format, int Version, T Content);

    public static string ToJson(MappingSpec spec) => Write(MappingFormat, spec);
    public static string ToJson(JobSpec job) => Write(JobFormat, job);
    public static byte[] ToBytes(MappingSpec spec) => Encoding.UTF8.GetBytes(ToJson(spec));
    public static byte[] ToBytes(JobSpec job) => Encoding.UTF8.GetBytes(ToJson(job));

    public static MappingSpec MappingFromJson(string json) => Read<MappingSpec>(MappingFormat, json);
    public static JobSpec JobFromJson(string json) => Read<JobSpec>(JobFormat, json);
    public static MappingSpec MappingFromBytes(ReadOnlySpan<byte> utf8) => MappingFromJson(Encoding.UTF8.GetString(utf8));
    public static JobSpec JobFromBytes(ReadOnlySpan<byte> utf8) => JobFromJson(Encoding.UTF8.GetString(utf8));

    static string Write<T>(string format, T content) => JsonSerializer.Serialize(new Envelope<T>(format, Version, content), Options);

    static T Read<T>(string format, string json)
    {
        Envelope<T>? env;
        try { env = JsonSerializer.Deserialize<Envelope<T>>(json, Options); }
        catch (JsonException e) { throw new InvalidDataException($"not a valid {format} config: {e.Message}", e); }
        if (env is null || env.Format != format) throw new InvalidDataException($"not a {format} config (format '{env?.Format}')");
        if (env.Version != Version) throw new InvalidDataException($"{format} version {env.Version} is not supported (expected {Version})");
        return env.Content ?? throw new InvalidDataException($"{format} config has no content");
    }
}
