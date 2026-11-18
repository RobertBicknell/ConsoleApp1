using Csv2Xml.Runtime.Validation;

namespace Csv2Xml.Runtime.Run;

/// <summary>A document-level value the user supplies (or accepts a default for) when starting a run.</summary>
/// <param name="Key">Stable name used in requests and plugins.</param>
/// <param name="Path">Root field path it fills.</param>
/// <param name="Default">Literal or token: {now} {today} {guid} {machine} {user}. Used when nothing is supplied.</param>
public sealed record RunParameterDef(string Key, string Path, string Label, string DataType, bool Required, string? Default = null);

public sealed record ResolvedParameters(IReadOnlyDictionary<string, string> ByKey, IReadOnlyDictionary<string, string> ByPath);

public static class RunParameters
{
    public static string Expand(string? value, TimeProvider clock) => value switch
    {
        null => "",
        "{now}" => clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        "{today}" => clock.GetUtcNow().ToString("yyyy-MM-dd"),
        "{guid}" => Guid.NewGuid().ToString(),
        "{machine}" => Environment.MachineName,
        "{user}" => Environment.UserName,
        _ => value,
    };

    /// <summary>Supplied value wins, else expanded default. Reports every problem (unknown key, missing required, bad type) at once.</summary>
    public static ResolvedParameters Resolve(IReadOnlyList<RunParameterDef> defs, IReadOnlyDictionary<string, string>? supplied, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        supplied ??= new Dictionary<string, string>();
        var problems = new List<string>();
        var byKey = new Dictionary<string, string>();
        var byPath = new Dictionary<string, string>();

        problems.AddRange(supplied.Keys.Where(k => defs.All(d => d.Key != k)).Select(k => $"unknown parameter '{k}'"));

        foreach (var d in defs)
        {
            var v = supplied.TryGetValue(d.Key, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : Expand(d.Default, clock);
            if (string.IsNullOrWhiteSpace(v))
            {
                if (d.Required) problems.Add($"'{d.Label}' is required");
                continue;
            }
            if (TypeCheck.Error(d.DataType, v) is { } err) { problems.Add($"'{d.Label}': {err}"); continue; }
            byKey[d.Key] = v; byPath[d.Path] = v;
        }

        if (problems.Count > 0) throw new RunException(problems);
        return new ResolvedParameters(byKey, byPath);
    }
}
