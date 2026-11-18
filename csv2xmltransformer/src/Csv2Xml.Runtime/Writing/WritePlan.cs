using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Runtime.Writing;

/// <summary>Element/attribute template node. FieldIndex binds a node to a spec field (its text or attribute value).</summary>
sealed class Node(string name, bool isAttribute)
{
    public string Name { get; } = name;
    public bool IsAttribute { get; } = isAttribute;
    public int FieldIndex { get; set; } = -1;
    public List<Node> Kids { get; } = [];

    public Node Child(string name, bool attr)
    {
        var k = Kids.Find(x => x.Name == name && x.IsAttribute == attr);
        if (k is null) Kids.Add(k = new Node(name, attr));
        return k;
    }
}

/// <summary>Root-field template builder.</summary>
static class RootTemplate
{
    public static Node Build(string rootName, IReadOnlyList<RootFieldSpec> fields)
    {
        var root = new Node(rootName, false);
        for (var i = 0; i < fields.Count; i++)
        {
            var node = root;
            foreach (var seg in fields[i].Path.Split('/').Skip(1))
                node = seg.StartsWith('@') ? node.Child(seg[1..], true) : node.Child(seg, false);
            node.FieldIndex = i;
        }
        return root;
    }
}

/// <param name="Wrappers">Non-repeating elements between the parent level and this level's element, outermost first.
/// Each carries its own mapped content (not the next wrapper), written when it opens, so the element is written once.</param>
/// <param name="Template">Owned fields as a tree; its Name is the level's element name.</param>
/// <param name="KeyFields">Field indices whose values identify an instance.</param>
sealed record Level(string Path, Node[] Wrappers, Node Template, int[] KeyFields);

/// <summary>
/// Static analysis of a spec: root template + chain of repeating levels (outermost first).
/// Only mapped fields shape the plan: an unmapped field never produces output, so it cannot constrain the structure.
/// </summary>
sealed class WritePlan
{
    public string RootName { get; }
    public Node RootTemplate { get; }
    public IReadOnlyList<Level> Levels { get; }

    public WritePlan(MappingSpec spec)
    {
        var fields = spec.Fields;
        RootName = fields.Count > 0 ? fields[0].Path.Split('/')[0] : spec.Kind;
        var mapped = Enumerable.Range(0, fields.Count).Where(i => fields[i].ColumnIndex is not null).ToArray();

        var chain = RepeatChain.Of(mapped.Select(i => fields[i].RepeatingAncestors));
        if (RepeatChain.FirstBranch(mapped.Select(i => (fields[i].Path, fields[i].RepeatingAncestors))) is { } branch)
            throw new NotSupportedException($"Mapped values sit in sibling repeating groups ('{branch}' is outside '{string.Join(" > ", chain)}'); one CSV row cannot express both.");

        RootTemplate = new Node(RootName, false);
        var levels = new List<(string Path, Node Node)>(chain.Select(p => (p, new Node(p.Split('/')[^1], false))));

        foreach (var i in mapped)
        {
            var f = fields[i];
            var depth = f.RepeatingAncestors.Count;
            var basePath = depth == 0 ? RootName : f.RepeatingAncestors[^1];
            var node = depth == 0 ? RootTemplate : levels[depth - 1].Node;
            var rel = f.Path.Split('/').Skip(basePath.Split('/').Length).ToArray();
            foreach (var seg in rel)
                node = seg.StartsWith('@') ? node.Child(seg[1..], true) : node.Child(seg, false);
            node.FieldIndex = i;
        }

        Levels = levels.Select((l, i) =>
        {
            var parent = i == 0 ? RootName : levels[i - 1].Path;
            var names = l.Path.Split('/').Skip(parent.Split('/').Length).SkipLast(1);
            var wrappers = DetachWrappers(i == 0 ? RootTemplate : levels[i - 1].Node, names);
            var owned = mapped.Where(k => fields[k].RepeatingAncestors.Count == i + 1).ToArray();
            int[] key = spec.GroupKeys is { } gk && gk.TryGetValue(l.Path, out var keyNames)
                ? keyNames.Select(n => Array.FindIndex(fields.ToArray(), f => f.Path == n)).Where(k => k >= 0).ToArray()
                : owned;
            return new Level(l.Path, wrappers, l.Node, key);
        }).ToList();
    }

    /// <summary>
    /// Moves the wrapper chain out of the parent's template: a wrapper that also holds mapped values (e.g. P with F, T beside
    /// repeating S) would otherwise be written twice, once by the parent's template and once around the repeating level.
    /// Each returned node keeps only its own content; the next wrapper is detached from it in turn.
    /// </summary>
    static Node[] DetachWrappers(Node parent, IEnumerable<string> names)
    {
        var chain = new List<Node>();
        var holder = parent;
        foreach (var name in names)
        {
            var n = holder.Kids.Find(k => k.Name == name && !k.IsAttribute);
            if (n is null) n = new Node(name, false);
            else holder.Kids.Remove(n);
            chain.Add(n);
            holder = n;
        }
        return [.. chain];
    }
}

/// <summary>
/// The single rule for how CSV rows can express repetition: all values must lie on one chain of nested repeating elements
/// (e.g. Order > Line). Shared by the writer and the design-time checks so they cannot disagree.
/// </summary>
public static class RepeatChain
{
    /// <summary>The deepest chain among the given repeating-ancestor lists.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<IReadOnlyList<string>> repeatingAncestors) =>
        repeatingAncestors.OrderByDescending(a => a.Count).FirstOrDefault() ?? [];

    /// <summary>Path of the first item not on the deepest chain (i.e. in a sibling repeating group), or null when all fit.</summary>
    public static string? FirstBranch(IEnumerable<(string Path, IReadOnlyList<string> RepeatingAncestors)> items)
    {
        var list = items.ToList();
        var chain = Of(list.Select(x => x.RepeatingAncestors));
        return list.FirstOrDefault(x => !x.RepeatingAncestors.SequenceEqual(chain.Take(x.RepeatingAncestors.Count))).Path;
    }
}
