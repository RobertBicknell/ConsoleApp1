using System.Text.RegularExpressions;
using ToolRunner.Hosts;

namespace ToolRunner.Core;

/// One service's image and the version read from its tag. Variable is the ${NAME} the compose file takes the tag from.
public sealed record ImageVersion(string Stack, string Service, string Image, string Tag, string? Version, string? Variable);

/// Product = the images whose version is "the stack's version": those tagged through a variable, else those with a
/// recognisable version. Detected = the version most of them carry; Odd = product images on another version.
public sealed record StackVersions(IReadOnlyList<ImageVersion> Images, IReadOnlyList<ImageVersion> Product, string? Detected, string? Variable, IReadOnlyList<ImageVersion> Odd);

/// How versions are written here. The version is the APPLICATION's version: the one the image was pushed with.
/// A number that belongs to the base image (alpine3.20, bookworm, ...) is never it. Everything real-world-specific is in these settings:
///   VERSION_PATTERN      regex that finds the version inside an image tag (default: dotted numbers, or "latest")
///   VERSION_IGNORE       regex for the parts of a tag that describe the base image; removed before the pattern is applied
///   VERSION_GIT_REFS     `;`-separated git ref templates tried in order for a version, {version} = the version
///   VERSION_LATEST_REFS  `;`-separated refs that "latest" means
public sealed record VersionScheme(Regex Pattern, IReadOnlyList<string> RefTemplates, IReadOnlyList<string> LatestRefs, Regex Ignore)
{
    public const string DefaultIgnore = @"(alpine|debian|ubuntu|ubi|bookworm|bullseye|jammy|noble|musl|net|dotnet|aspnet)[-_.]?v?\d+(\.\d+)*";
    public const string DefaultPattern = @"\d+(\.\d+)+|latest";
    public const string DefaultRefs = "v{version};{version};release/{version};origin/release/{version}";
    public const string DefaultLatest = "origin/master;origin/main;master;main";

    public static VersionScheme From(Func<string, string?> var) => new(
        new Regex(var("VERSION_PATTERN") is { Length: > 0 } p ? p : DefaultPattern, RegexOptions.IgnoreCase),
        Split(var("VERSION_GIT_REFS") is { Length: > 0 } r ? r : DefaultRefs),
        Split(var("VERSION_LATEST_REFS") is { Length: > 0 } l ? l : DefaultLatest),
        new Regex(var("VERSION_IGNORE") is { Length: > 0 } g ? g : DefaultIgnore, RegexOptions.IgnoreCase));

    private static List<string> Split(string s) => s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// The application version inside a tag: "1.4.2-alpine3.20" -> "1.4.2", "alpine3.20-1.4.2" -> "1.4.2"; null when the tag carries none.
    public string? Of(string tag) => Pattern.Match(Ignore.Replace(tag, "")) is { Success: true } m ? m.Value : null;

    /// The git refs a version may be found under, in the order to try them.
    public IReadOnlyList<string> RefsFor(string version) =>
        version.Equals("latest", StringComparison.OrdinalIgnoreCase) ? LatestRefs : RefTemplates.Select(t => t.Replace("{version}", version)).ToList();

    /// The version a ref name stands for under the templates ("release/1.4" -> "1.4"); null if it fits none.
    public string? VersionOfRef(string name)
    {
        foreach (var t in RefTemplates)
        {
            var m = Regex.Match(name, "^" + Regex.Escape(t).Replace(Regex.Escape("{version}"), "(.+)") + "$");
            if (m.Success && Of(m.Groups[1].Value) == m.Groups[1].Value) return m.Groups[1].Value;
        }
        return null;
    }
}

public sealed record GitVersion(string Version, string Ref, string Kind);     // Kind: tag | branch

public static class Versions
{
    /// Reads each service's version from its (already expanded) image, and the variable behind the tag from the raw compose text.
    public static List<ImageVersion> Read(string stack, IEnumerable<ServiceInfo> services, IReadOnlyDictionary<string, string> composeTexts, VersionScheme scheme)
    {
        var rows = new List<ImageVersion>();
        foreach (var s in services.Where(s => s.Image is not null))
        {
            var tag = ImageRef.Parse(s.Image!).Tag;
            string? variable = null;
            if (composeTexts.TryGetValue(s.File, out var text))
            {
                // the raw `image:` line of this service: the first one at or after the service's own line
                var raw = text.Split('\n').Skip(s.Line).Select(l => Regex.Match(l, @"^\s+image:\s*(\S.*)$")).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "";
                var masked = Regex.Replace(raw, @"\$\{[^}]*\}", m => new string('_', m.Length));     // a ${X:-d} must not look like the tag separator
                var colon = masked.LastIndexOf(':');
                variable = colon > masked.LastIndexOf('/') ? Regex.Matches(raw[colon..], @"\$\{([A-Za-z_]\w*)").LastOrDefault()?.Groups[1].Value : null;
            }
            rows.Add(new ImageVersion(stack, s.Name, s.Image!, tag, scheme.Of(tag), variable));
        }
        return rows;
    }

    public static StackVersions Summarise(IReadOnlyList<ImageVersion> images)
    {
        var product = images.Where(i => i.Variable is not null && i.Version is not null).ToList();
        if (product.Count == 0) product = images.Where(i => i.Version is not null).ToList();
        var detected = product.GroupBy(i => i.Version!).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        var variable = product.Where(i => i.Variable is not null).GroupBy(i => i.Variable!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        return new StackVersions(images, product, detected, variable, product.Where(i => i.Version != detected).ToList());
    }

    private static async Task<List<string>> Git(IHost host, string repo, CancellationToken ct, params string[] args)
    {
        var r = await host.ExecAsync("git", new[] { "-C", repo }.Concat(args).ToList(), null, null, ct);
        return r.Code == 0 ? r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : new();
    }

    /// The first ref of the scheme that exists in the repo for this version, and its commit. Null when none does.
    public static async Task<(string Ref, string Commit)?> ResolveAsync(IHost host, string repo, string version, VersionScheme scheme, CancellationToken ct)
    {
        foreach (var name in scheme.RefsFor(version))
            if ((await Git(host, repo, ct, "rev-parse", "--verify", "--quiet", name + "^{commit}")).FirstOrDefault() is { } commit) return (name, commit);
        return null;
    }

    /// The versions the repo has refs for (tags and branches that fit the scheme), newest first; plus "latest" if a latest ref exists.
    public static async Task<List<GitVersion>> FromGitAsync(IHost host, string repo, VersionScheme scheme, CancellationToken ct)
    {
        var found = new List<GitVersion>();
        foreach (var line in await Git(host, repo, ct, "for-each-ref", "--sort=-creatordate", "--format=%(refname)", "refs/tags", "refs/heads", "refs/remotes"))
        {
            var kind = line.StartsWith("refs/tags/") ? "tag" : "branch";
            var name = Regex.Replace(line, @"^refs/(tags|heads|remotes)/", "");
            if (scheme.VersionOfRef(name) is { } v && found.All(f => f.Version != v)) found.Add(new GitVersion(v, name, kind));
        }
        if (await ResolveAsync(host, repo, "latest", scheme, ct) is { } latest) found.Insert(0, new GitVersion("latest", latest.Ref, "branch"));
        return found;
    }

    public static async Task<string?> HeadAsync(IHost host, string repo, CancellationToken ct) => (await Git(host, repo, ct, "rev-parse", "HEAD")).FirstOrDefault();

    /// The repository path inside the registry for an image: without the registry prefix (or a leading host name).
    public static string RegistryRepo(string image, string? prefix)
    {
        var repo = ImageRef.Parse(image).Repo;
        if (!string.IsNullOrEmpty(prefix) && repo.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return repo[(prefix.TrimEnd('/').Length + 1)..];
        var first = repo.Split('/')[0];
        return repo.Contains('/') && (first.Contains('.') || first.Contains(':')) ? repo[(first.Length + 1)..] : repo;
    }

    /// Versions among a repository's tags, highest first (numeric where both are numeric).
    public static List<string> FromTags(IEnumerable<string> tags, VersionScheme scheme) =>
        tags.Select(scheme.Of).OfType<string>().Distinct().OrderByDescending(v => v, Comparer<string>.Create(Compare)).ToList();

    public static int Compare(string a, string b)
    {
        if (a == b) return 0;
        if (a.Equals("latest", StringComparison.OrdinalIgnoreCase)) return 1;
        if (b.Equals("latest", StringComparison.OrdinalIgnoreCase)) return -1;
        var (x, y) = (a.Split('.'), b.Split('.'));
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var (p, q) = (i < x.Length ? x[i] : "0", i < y.Length ? y[i] : "0");
            var c = int.TryParse(p, out var n) && int.TryParse(q, out var m) ? n.CompareTo(m) : string.CompareOrdinal(p, q);
            if (c != 0) return c;
        }
        return 0;
    }
}
