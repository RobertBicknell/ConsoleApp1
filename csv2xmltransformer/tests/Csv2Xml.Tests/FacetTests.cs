using System.Xml;
using System.Xml.Schema;
using Csv2Xml.Design;
using Csv2Xml.Design.Schema;

namespace Csv2Xml.Tests;

public class FacetTests
{
    public static readonly string Dir = TestKit.SamplesDir;
    public static readonly string RestrictedXsd = File.ReadAllText(Path.Combine(Dir, "restricted.xsd"));
    public static readonly string RestrictedExample = File.ReadAllText(Path.Combine(Dir, "restricted.example.xml"));
    static readonly SchemaDocument Doc = MappingDesigner.LoadSchema(RestrictedXsd);

    static SchemaTarget T(string path) => Doc.Kinds.SelectMany(k => k.Targets).Concat(Doc.RootTargets).Single(t => t.Path == path);

    [Fact]
    public void Example_xml_is_valid_against_restricted_xsd()
    {
        var set = new XmlSchemaSet();
        set.Add(null, XmlReader.Create(new StringReader(RestrictedXsd)));
        var errors = new List<string>();
        var doc = System.Xml.Linq.XDocument.Parse(RestrictedExample);
        System.Xml.Schema.Extensions.Validate(doc, set, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors);
    }

    [Fact]
    public void Reads_kinds_root_targets_and_three_level_chain()
    {
        Assert.Equal(["Shipments", "Depots"], Doc.Kinds.Select(k => k.Name));
        Assert.Equal(["Manifest/@issued", "Manifest/Depot"], Doc.RootTargets.Select(t => t.Path).Order());
        Assert.Equal(["Shipments/Shipment", "Shipments/Shipment/Parcel", "Shipments/Shipment/Parcel/Item"],
            T("Shipments/Shipment/Parcel/Item/@code").RepeatingAncestors);
    }

    [Fact]
    public void Derived_named_type_combines_length_and_pattern()
    {
        var f = T("Shipments/Shipment/Origin").Facets!;
        Assert.Equal(["[A-Z]{3}-[0-9]{4}"], f.Patterns);
        Assert.Equal(2, f.MinLength);
        Assert.Equal(12, f.MaxLength);
    }

    [Fact]
    public void Enumeration_ranges_digits_and_fixed()
    {
        Assert.Equal(["NEW", "SHIPPED", "LOST"], T("Shipments/Shipment/Status").Facets!.Enumeration);

        var w = T("Shipments/Shipment/Parcel/WeightKg").Facets!;
        Assert.Equal(("1", "1000"), (w.MinInclusive, w.MaxExclusive));

        var money = T("Shipments/Shipment/Parcel/Item/Value").Facets!;
        Assert.Equal((7, 2, "0"), (money.TotalDigits, money.FractionDigits, money.MinInclusive));

        Assert.Equal("GBP", T("Shipments/Shipment/Insured/@currency").Facets!.Fixed);
    }

    [Fact]
    public void Simple_content_element_inherits_base_facets_and_type()
    {
        var ins = T("Shipments/Shipment/Insured");
        Assert.Equal("decimal", ins.DataType);
        Assert.Equal(2, ins.Facets!.FractionDigits);
    }

    [Fact]
    public void Unrestricted_types_have_no_facets()
    {
        Assert.Null(T("Shipments/Shipment/Parcel/Item/@code").Facets);
        Assert.Null(TestKit.Doc.Kinds.Single(k => k.Name == "Customers").Targets.Single(t => t.Name == "City").Facets);
    }
}
