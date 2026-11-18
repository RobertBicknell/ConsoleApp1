using System.Xml;
using System.Xml.Schema;
using Csv2Xml.Runtime.Validation;

namespace Csv2Xml.Design.Schema;

/// <summary>
/// Compiles an XSD and flattens each permitted document kind into mappable targets.
/// Kind discovery: the first global element (file order, not used by ref elsewhere) whose content has a choice yields one kind
/// per branch, and everything
/// else in that element (attributes, sibling elements) becomes root targets; other global elements are helpers. With no such
/// element, each global element is a kind.
/// </summary>
public static class SchemaReader
{
    const int MaxDepth = 24;

    public static SchemaDocument ReadDocumentFile(string xsdPath) => ReadDocumentText(File.ReadAllText(xsdPath));

    /// <exception cref="InvalidDataException">The XSD is invalid, references unresolvable includes/imports, or mixes element forms.</exception>
    public static SchemaDocument ReadDocumentText(string xsd)
    {
        XmlSchemaSet set;
        try { set = XsdValidator.Compile(xsd); }
        catch (Exception e) when (e is XmlSchemaException or XmlException) { throw new InvalidDataException("XSD invalid: " + e.Message, e); }

        var hash = XsdValidator.Hash(xsd);
        var globals = set.GlobalElements.Values.Cast<XmlSchemaElement>().ToList();
        var locals = new HashSet<string>();

        SchemaDocument doc;
        if (ChoiceRoot(set) is var (root, choice))
        {
            var rootTargets = new List<SchemaTarget>();
            Walk(root, null, true, [], 0, new Ctx(rootTargets, locals), skip: choice);
            var kinds = choice.Items.OfType<XmlSchemaElement>()
                .Select(b => new SchemaKind(b.QualifiedName.Name, Flatten(b, locals), root.Name, b.QualifiedName.Namespace)).ToList();
            locals.UnionWith(kinds.Select(k => k.Namespace ?? "")); // choice branches are local elements too
            doc = new SchemaDocument(root.Name!, root.QualifiedName.Namespace, rootTargets, kinds, hash);
        }
        else
            doc = new SchemaDocument("", globals.FirstOrDefault()?.QualifiedName.Namespace, [],
                globals.Select(g => new SchemaKind(g.QualifiedName.Name, Flatten(g, locals), null, g.QualifiedName.Namespace)).ToList(), hash);

        if (locals.Count > 1)
            throw new InvalidDataException($"XSD mixes qualified and unqualified local elements (namespaces: {string.Join(", ", locals.Select(n => n.Length == 0 ? "(none)" : n))}); not supported");
        return doc with { LocalNamespace = locals.SingleOrDefault() ?? doc.Namespace };
    }

    /// <summary>Accumulators for one walk: targets found, and namespaces of local (non-global) elements seen.</summary>
    sealed record Ctx(List<SchemaTarget> Targets, HashSet<string> LocalNamespaces);

    /// <summary>
    /// The document root that carries the kinds: the first global element, in file order, that is not used inside another
    /// element (via ref) and whose own content has a choice of two or more elements. Other global elements are helpers.
    /// </summary>
    static (XmlSchemaElement Root, XmlSchemaChoice Choice)? ChoiceRoot(XmlSchemaSet set)
    {
        var ordered = set.Schemas().Cast<XmlSchema>().SelectMany(s => s.Items.OfType<XmlSchemaElement>()).ToList();
        var referenced = new HashSet<XmlQualifiedName>();
        var seen = new HashSet<XmlSchemaType>();
        foreach (var g in ordered) CollectRefs(g.ElementSchemaType, referenced, seen);
        return ordered.Where(g => !referenced.Contains(g.QualifiedName))
            .Select(g => (Root: g, Choice: FindChoice(g)))
            .Where(x => x.Choice is not null)
            .Select(x => ((XmlSchemaElement, XmlSchemaChoice)?)(x.Root, x.Choice!))
            .FirstOrDefault();
    }

    static void CollectRefs(XmlSchemaType? t, HashSet<XmlQualifiedName> refs, HashSet<XmlSchemaType> seen)
    {
        if (t is not XmlSchemaComplexType { ContentTypeParticle: { } p } || !seen.Add(t)) return;
        Visit(p);

        void Visit(XmlSchemaParticle q)
        {
            switch (q)
            {
                case XmlSchemaElement e:
                    if (!e.RefName.IsEmpty) refs.Add(e.RefName);
                    CollectRefs(e.ElementSchemaType, refs, seen);
                    break;
                case XmlSchemaGroupBase g:
                    foreach (var i in g.Items.OfType<XmlSchemaParticle>()) Visit(i);
                    break;
            }
        }
    }

    static XmlSchemaChoice? FindChoice(XmlSchemaElement g) =>
        g.ElementSchemaType is XmlSchemaComplexType { ContentTypeParticle: { } p } ? FindChoice(p) : null;

    static XmlSchemaChoice? FindChoice(XmlSchemaParticle p) => p switch
    {
        XmlSchemaChoice c when c.Items.OfType<XmlSchemaElement>().Count() > 1 => c,
        XmlSchemaGroupBase g => g.Items.OfType<XmlSchemaParticle>().Select(FindChoice).FirstOrDefault(x => x is not null),
        _ => null,
    };

    /// <summary>Paths start with the kind element name.</summary>
    static IReadOnlyList<SchemaTarget> Flatten(XmlSchemaElement kindRoot, HashSet<string> locals)
    {
        var acc = new List<SchemaTarget>();
        Walk(kindRoot, parentPath: null, required: true, repeating: [], depth: 0, new Ctx(acc, locals));
        return acc;
    }

    static void Walk(XmlSchemaElement el, string? parentPath, bool required, IReadOnlyList<string> repeating, int depth, Ctx ctx, XmlSchemaParticle? skip = null)
    {
        if (depth > MaxDepth) return;
        var name = el.QualifiedName.Name;
        if (parentPath is not null) ctx.LocalNamespaces.Add(el.QualifiedName.Namespace); // compiled name reflects elementFormDefault/form
        var path = parentPath is null ? name : parentPath + "/" + name;
        var reps = el.MaxOccurs > 1 ? [.. repeating, path] : repeating;
        var type = el.ElementSchemaType;
        var complex = type as XmlSchemaComplexType;

        var hasSimpleValue = type is XmlSchemaSimpleType || complex is { ContentType: XmlSchemaContentType.TextOnly };
        if (hasSimpleValue)
            ctx.Targets.Add(new SchemaTarget(path, name, TargetKind.Element, TypeName(type), required, reps, WithFixed(FacetsOf(type), el.FixedValue)));

        if (complex is null) return;

        foreach (XmlSchemaAttribute a in complex.AttributeUses.Values.Cast<XmlSchemaAttribute>().Where(a => a.Use != XmlSchemaUse.Prohibited))
            ctx.Targets.Add(new SchemaTarget(path + "/@" + a.QualifiedName.Name, a.QualifiedName.Name, TargetKind.Attribute,
                TypeName(a.AttributeSchemaType), required && a.Use == XmlSchemaUse.Required, reps, WithFixed(FacetsOf(a.AttributeSchemaType), a.FixedValue)));

        if (complex.ContentTypeParticle is { } p) WalkParticle(p, path, required, reps, depth + 1, ctx, skip);
    }

    static void WalkParticle(XmlSchemaParticle p, string path, bool required, IReadOnlyList<string> reps, int depth, Ctx ctx, XmlSchemaParticle? skip)
    {
        if (ReferenceEquals(p, skip)) return;
        var req = required && p.MinOccurs >= 1;
        switch (p)
        {
            case XmlSchemaElement e:
                Walk(e, path, req, reps, depth, ctx);
                break;
            case XmlSchemaChoice c:
                foreach (var i in c.Items.OfType<XmlSchemaParticle>()) WalkParticle(i, path, false, reps, depth, ctx, skip);
                break;
            case XmlSchemaGroupBase g: // sequence / all
                foreach (var i in g.Items.OfType<XmlSchemaParticle>()) WalkParticle(i, path, req, reps, depth, ctx, skip);
                break;
        }
    }

    /// <summary>
    /// Restriction facets along the derivation chain, most-derived first. Single-valued facets: most-derived wins.
    /// Enumeration: most-derived non-empty set. Patterns: accumulated (XSD requires all to match).
    /// </summary>
    static Facets FacetsOf(XmlSchemaType? type)
    {
        var all = new List<XmlSchemaFacet>();
        IReadOnlyList<string> enumeration = [];
        for (var t = type; t is not null; t = t.BaseXmlSchemaType)
        {
            var level = (t switch
            {
                XmlSchemaSimpleType { Content: XmlSchemaSimpleTypeRestriction r } => r.Facets,
                XmlSchemaComplexType { ContentModel: XmlSchemaSimpleContent { Content: XmlSchemaSimpleContentRestriction r } } => r.Facets,
                _ => null,
            })?.OfType<XmlSchemaFacet>().ToList() ?? [];
            if (enumeration.Count == 0) enumeration = level.OfType<XmlSchemaEnumerationFacet>().Select(f => f.Value!).ToList();
            all.AddRange(level);
        }
        if (all.Count == 0) return Facets.None;

        string? S<T>() where T : XmlSchemaFacet => all.OfType<T>().FirstOrDefault()?.Value;
        int? I<T>() where T : XmlSchemaFacet => int.TryParse(S<T>(), out var v) ? v : null;
        return new Facets(enumeration, all.OfType<XmlSchemaPatternFacet>().Select(f => f.Value!).ToList(),
            I<XmlSchemaLengthFacet>(), I<XmlSchemaMinLengthFacet>(), I<XmlSchemaMaxLengthFacet>(),
            S<XmlSchemaMinInclusiveFacet>(), S<XmlSchemaMaxInclusiveFacet>(), S<XmlSchemaMinExclusiveFacet>(), S<XmlSchemaMaxExclusiveFacet>(),
            I<XmlSchemaTotalDigitsFacet>(), I<XmlSchemaFractionDigitsFacet>());
    }

    static Facets? WithFixed(Facets f, string? fixedValue) => fixedValue is null ? (f == Facets.None ? null : f) : f with { Fixed = fixedValue };

    static string TypeName(XmlSchemaType? t) => t is null ? "string" : t.TypeCode.ToString().ToLowerInvariant();
}
