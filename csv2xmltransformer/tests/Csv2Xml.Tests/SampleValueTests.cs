using System.Xml.Linq;
using Csv2Xml.Design;
using Csv2Xml.Design.Samples;
using Csv2Xml.Design.Schema;

namespace Csv2Xml.Tests;

public class SampleValueTests
{
    static readonly SchemaDocument Doc = MappingDesigner.LoadSchema(FacetTests.RestrictedXsd);
    static SchemaTarget T(string path) => Doc.Kinds.SelectMany(k => k.Targets).Concat(Doc.RootTargets).Single(t => t.Path == path);

    [Theory]
    [InlineData("[A-Z]{3}-[0-9]{4}")]
    [InlineData("\\d{2}[a-c]?X+")]
    [InlineData("(AB|CD)\\.\\d{1,3}")]
    [InlineData("[A-Z0-9]{8}")]
    [InlineData("GB\\d{9}|GB\\d{12}")]
    public void Pattern_samples_match_their_pattern_and_vary(string pattern)
    {
        var a = PatternSample.Generate(pattern, 1);
        var b = PatternSample.Generate(pattern, 2);
        Assert.NotNull(a);
        Assert.True(PatternSample.Matches(pattern, a!), a);
        Assert.True(PatternSample.Matches(pattern, b!), b);
    }

    [Theory]
    [InlineData("\\p{Lu}+")]
    [InlineData("[^a]")]
    public void Unsupported_patterns_return_null(string pattern) => Assert.Null(PatternSample.Generate(pattern, 1));

    [Fact]
    public void Synthetic_values_honour_facets()
    {
        for (var n = 1; n <= 20; n++)
        {
            var origin = SyntheticValues.For(T("Shipments/Shipment/Origin"), n);
            Assert.True(origin.Certain);
            Assert.Matches("^[A-Z]{3}-[0-9]{4}$", origin.Text);

            Assert.Contains(SyntheticValues.For(T("Shipments/Shipment/Status"), n).Text, new[] { "NEW", "SHIPPED", "LOST" });

            var w = int.Parse(SyntheticValues.For(T("Shipments/Shipment/Parcel/WeightKg"), n).Text);
            Assert.InRange(w, 1, 999);

            var desc = SyntheticValues.For(T("Shipments/Shipment/Parcel/Item/Description"), n).Text;
            Assert.InRange(desc.Length, 2, 12);

            var money = decimal.Parse(SyntheticValues.For(T("Shipments/Shipment/Parcel/Item/Value"), n).Text, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(money >= 0);

            Assert.True(int.Parse(SyntheticValues.For(T("Shipments/Shipment/Parcel/@parcelNo"), n).Text) >= 1);
        }
        Assert.Equal("GBP", SyntheticValues.For(T("Shipments/Shipment/Insured/@currency"), 3).Text);
    }

    [Fact]
    public void Exemplar_rows_flatten_three_level_chain()
    {
        var kind = Doc.Kinds.Single(k => k.Name == "Shipments");
        var chain = kind.Targets.Select(t => t.RepeatingAncestors).MaxBy(a => a.Count)!;
        var rows = ExemplarValues.Rows(kind, chain, XDocument.Parse(FacetTests.RestrictedExample))!;
        Assert.Equal(4, rows.Count); // BK-1, LP-7, MG-2, DK-9
        Assert.Equal(["SHP-1001", "SHP-1001", "SHP-1001", "SHP-1002"], rows.Select(r => r["Shipments/Shipment/@ref"]));
        Assert.Equal(["1", "1", "2", "1"], rows.Select(r => r["Shipments/Shipment/Parcel/@parcelNo"]));
        Assert.False(rows[1].ContainsKey("Shipments/Shipment/Parcel/Item/Value")); // Lamp has no value
        Assert.Equal("250.00", rows[0]["Shipments/Shipment/Insured"]);
        Assert.Equal("GBP", rows[0]["Shipments/Shipment/Insured/@currency"]);
    }

    [Fact]
    public void Exemplar_of_another_kind_yields_no_rows_and_root_values_are_read()
    {
        var xml = XDocument.Parse(FacetTests.RestrictedExample);
        var depots = Doc.Kinds.Single(k => k.Name == "Depots");
        Assert.False(ExemplarValues.Contains(depots, xml));
        Assert.Empty(ExemplarValues.Rows(depots, ["Depots/DepotInfo"], xml));
        var rv = ExemplarValues.RootValues(Doc, xml);
        Assert.Equal("LDS-0001", rv["Manifest/Depot"]);
        Assert.Equal("2026-03-01T09:30:00Z", rv["Manifest/@issued"]);
    }
}
