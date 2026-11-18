using Csv2Xml.Design.Samples;
using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Tests;

public class SampleGeneratorTests
{
    static readonly string DevXsd = TestKit.XsdText;

    static string Report(SampleSet s) => s.Readme();

    [Fact]
    public void Dev_xsd_without_exemplar_all_scenarios_behave_as_expected()
    {
        var s = SampleGenerator.Generate(DevXsd);
        Assert.True(s.AllPassed, Report(s));
        Assert.Equal(["Customers", "Orders", "Products", "Invoices"], s.Files.Select(f => f.Kind).Distinct());
        var orders = s.Files.Where(f => f.Kind == "Orders").Select(f => f.Scenario).ToList();
        Assert.Equal(["clean", "noisy", "hierarchy", "duplicate_headers", "abbreviated", "reordered", "bad_data"], orders); // all Orders items are required
        Assert.Contains(s.Files, f => f.Kind == "Customers" && f.Scenario == "optional_empty");
    }

    [Fact]
    public void Restricted_xsd_synthetic_values_satisfy_all_facets()
    {
        var s = SampleGenerator.Generate(FacetTests.RestrictedXsd);
        Assert.True(s.AllPassed, Report(s));
        Assert.DoesNotContain(s.Notes, n => n.Contains("pattern"));
        Assert.Matches("^[A-Z]{3}-[0-9]{4}$", s.RunValues["Manifest/Depot"]);
    }

    [Fact]
    public void Restricted_xsd_with_exemplar_uses_exemplar_values_and_notes_missing_kind()
    {
        var s = SampleGenerator.Generate(FacetTests.RestrictedXsd, FacetTests.RestrictedExample);
        Assert.True(s.AllPassed, Report(s));
        var clean = s.Files.Single(f => f.Kind == "Shipments" && f.Scenario == "clean");
        Assert.Contains("SHP-1001", clean.Csv);
        Assert.Contains("Books", clean.Verification.Xml);
        Assert.Equal("LDS-0001", s.RunValues["Manifest/Depot"]);
        Assert.Contains(s.Notes, n => n.StartsWith("Depots: not in the exemplar"));
    }

    [Fact]
    public void Hierarchy_groups_rows_into_nested_elements()
    {
        var s = SampleGenerator.Generate(FacetTests.RestrictedXsd);
        var h = s.Files.Single(f => f.Kind == "Shipments" && f.Scenario == "hierarchy");
        var x = System.Xml.Linq.XDocument.Parse(h.Verification.Xml);
        Assert.Equal(2, x.Descendants().Count(e => e.Name.LocalName == "Shipment"));
        Assert.Equal(4, x.Descendants().Count(e => e.Name.LocalName == "Parcel"));
        Assert.Equal(8, x.Descendants().Count(e => e.Name.LocalName == "Item"));
    }

    [Fact]
    public void Duplicate_header_mapping_points_at_the_real_column()
    {
        var f = SampleGenerator.Generate(DevXsd).Files.Single(f => f.Kind == "Products" && f.Scenario == "duplicate_headers");
        var hdr = f.Csv.Split('\n')[0].Split(',');
        Assert.Equal(hdr[0], hdr[1]);
        Assert.Equal(1, f.Mapping.Fields.First(x => x.Required).ColumnIndex);
    }

    [Fact]
    public void Files_round_trip_and_include_readme_mappings_and_expected_xml()
    {
        var s = SampleGenerator.Generate(DevXsd);
        var files = s.ToFiles();
        Assert.Contains("README.md", files.Keys);
        Assert.Contains("Orders.clean.csv", files.Keys);
        Assert.Contains("Orders.clean.expected.xml", files.Keys);
        Assert.DoesNotContain("Orders.bad_data.expected.xml", files.Keys);
        using (var ms = new MemoryStream(files["Orders.clean.expected.xml"])) Assert.Equal("Submission", System.Xml.Linq.XDocument.Load(ms).Root!.Name.LocalName); // bytes parse: declared encoding matches
        var spec = ConfigJson.MappingFromBytes(files["Orders.noisy.mapping.json"]);
        Assert.Equal("Orders", spec.Kind);
    }

    [Fact]
    public void Branching_repeats_are_skipped_with_a_note()
    {
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Order"><xs:complexType><xs:sequence>
                <xs:element name="Line" maxOccurs="unbounded" type="xs:string"/>
                <xs:element name="Note" maxOccurs="unbounded" type="xs:string"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var s = SampleGenerator.Generate(xsd);
        Assert.Empty(s.Files);
        Assert.Contains(s.Notes, n => n.StartsWith("Order: skipped") && n.Contains("Note"));
    }
}
