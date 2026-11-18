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
    public void Exemplar_generates_only_its_own_kind_from_its_own_values()
    {
        var s = SampleGenerator.Generate(FacetTests.RestrictedXsd, FacetTests.RestrictedExample);
        Assert.True(s.AllPassed, Report(s));
        Assert.Equal("Shipments", s.ExemplarKind);
        Assert.All(s.Files, f => Assert.Equal("Shipments", f.Kind)); // no Depots files invented
        var clean = s.Files.Single(f => f.Scenario == "clean");
        Assert.Contains("SHP-1001", clean.Csv);
        Assert.Contains("Books", clean.Verification.Xml);
        Assert.Equal("LDS-0001", s.RunValues["Manifest/Depot"]);
        Assert.DoesNotContain(s.Notes, n => n.Contains("synthesised") || n.Contains("Depots"));
        Assert.Contains("a Shipments document", s.Readme());
    }

    const string OrdersExemplar = """
        <Submission submissionId="S-1"><SubmitterName>Ann</SubmitterName><SubmittedAt>2026-01-02T03:04:05Z</SubmittedAt>
          <Orders><Order orderId="7" customerId="3"><OrderDate>2026-01-01</OrderDate>
            <Line sku="Q-1"><Quantity>4</Quantity><UnitPrice>2.25</UnitPrice></Line>
            <Line sku="Q-2"><Quantity>1</Quantity><UnitPrice>9.00</UnitPrice></Line>
          </Order></Orders></Submission>
        """;

    [Fact]
    public void Kind_is_read_from_the_exemplar_and_every_value_comes_from_it()
    {
        var s = SampleGenerator.Generate(DevXsd, OrdersExemplar);
        Assert.True(s.AllPassed, Report(s));
        Assert.Equal("Orders", s.ExemplarKind);
        Assert.Equal(["Orders"], s.Files.Select(f => f.Kind).Distinct());
        Assert.Equal("Ann", s.RunValues["Submission/SubmitterName"]);
        var clean = s.Files.Single(f => f.Scenario == "clean").Csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, clean.Length); // header + the exemplar's two lines, nothing more
        Assert.Contains("Q-2", clean[2]);
    }

    [Fact]
    public void Generated_csvs_never_carry_document_level_values()
    {
        foreach (var s in new[] { SampleGenerator.Generate(DevXsd), SampleGenerator.Generate(DevXsd, OrdersExemplar) })
            foreach (var f in s.Files)
            {
                var header = f.Csv.Split('\n')[0].ToLowerInvariant();
                Assert.DoesNotContain("submi", header); // submissionId, SubmitterName, SubmittedAt
                Assert.All(s.RunValues.Values, v => Assert.DoesNotContain(v, f.Csv));
                if (f.Expected == Expectation.Valid) Assert.Contains(s.RunValues.Values.First(), f.Verification.Xml); // they reach the XML as run values only
            }
    }

    [Fact]
    public void Optional_items_missing_from_the_exemplar_are_left_out_not_invented()
    {
        const string customers = """
            <Submission submissionId="S-1"><SubmitterName>Ann</SubmitterName><SubmittedAt>2026-01-02T03:04:05Z</SubmittedAt>
              <Customers><Customer id="1"><CustomerName>Bo</CustomerName></Customer></Customers></Submission>
            """;
        var s = SampleGenerator.Generate(DevXsd, customers);
        Assert.True(s.AllPassed, Report(s));
        var clean = s.Files.Single(f => f.Scenario == "clean");
        Assert.Equivalent(new[] { "Customers/Customer/@id", "Customers/Customer/CustomerName" }, clean.Mapping.Fields.Where(f => f.ColumnIndex is not null).Select(f => f.Path).ToArray(), strict: true);
        Assert.DoesNotContain("email", clean.Csv.Split('\n')[0]);
        Assert.Contains(s.Notes, n => n.Contains("'Customers/Customer/Email'") && n.Contains("left out"));
    }

    [Fact]
    public void Exemplar_invalid_against_the_xsd_is_refused()
    {
        var missingMandatory = OrdersExemplar.Replace("<OrderDate>2026-01-01</OrderDate>", "");
        var ex = Assert.Throws<InvalidDataException>(() => SampleGenerator.Generate(DevXsd, missingMandatory));
        Assert.StartsWith("exemplar XML is not valid against the XSD", ex.Message);
        Assert.Contains("OrderDate", ex.Message);

        var wrongRoot = Assert.Throws<InvalidDataException>(() => SampleGenerator.Generate(DevXsd, "<Other/>"));
        Assert.StartsWith("exemplar XML is not valid against the XSD", wrongRoot.Message);
    }

    [Fact]
    public void Xsd_only_generation_covers_every_kind_and_says_values_are_synthesised()
    {
        var s = SampleGenerator.Generate(DevXsd);
        Assert.Null(s.ExemplarKind);
        Assert.Contains(s.Notes, n => n.StartsWith("No exemplar XML"));
        foreach (var f in s.Files) // each file holds only its own kind's items
            Assert.All(f.Mapping.Fields, x => Assert.StartsWith(f.Kind + "/", x.Path));
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
