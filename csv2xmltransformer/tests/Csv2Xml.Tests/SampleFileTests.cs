using Csv2Xml.Design;
using Csv2Xml.Runtime.Csv;
using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Tests;

/// <summary>Each file in samples/csv behaves as samples/csv/README.md says.</summary>
public class SampleFileTests
{
    static readonly string Dir = Path.Combine(TestKit.SamplesDir, "csv");
    static readonly string Xsd = TestKit.XsdText;
    static readonly Design.Schema.SchemaDocument Doc = MappingDesigner.LoadSchema(Xsd);

    static string Text(string file) => File.ReadAllText(Path.Combine(Dir, file));
    static string[] Headers(string file) { using var r = new StringReader(Text(file)); return CsvParser.Read(r, ',').First(); }

    static Dictionary<string, int> Suggested(string kind, string[] h) =>
        MappingDesigner.Suggest(Doc, kind, h).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);

    static MappingSpec BuildSuggested(string kind, string file, Action<Dictionary<string, int>>? adjust = null)
    {
        var h = Headers(file);
        var a = Suggested(kind, h);
        adjust?.Invoke(a);
        var r = MappingDesigner.Build(Doc, kind, h, a);
        Assert.True(r.Success, string.Join("; ", r.Errors));
        return r.Spec!;
    }

    [Theory]
    [InlineData("customers_clean.csv", "Customers")]
    [InlineData("customers_noisy.csv", "Customers")]
    [InlineData("orders_clean.csv", "Orders")]
    [InlineData("products_clean.csv", "Products")]
    [InlineData("products_blank_header.csv", "Products")]
    [InlineData("invoices_clean.csv", "Invoices")]
    public void Suggestions_alone_produce_valid_xml(string file, string kind)
    {
        Assert.Equal(kind, MappingDesigner.RankKinds(Doc, Headers(file))[0].Kind);
        var v = MappingDesigner.Verify(Xsd, BuildSuggested(kind, file), Text(file));
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
    }

    [Fact]
    public void Noisy_extra_columns_stay_unmapped()
    {
        var s = Suggested("Customers", Headers("customers_noisy.csv"));
        Assert.DoesNotContain(4, s.Values); // Town
        Assert.DoesNotContain(7, s.Values); // Notes
    }

    [Fact]
    public void Duplicate_headers_need_and_honour_a_manual_choice()
    {
        var h = Headers("orders_duplicate_headers.csv");
        Assert.Equal(["date (#3)", "date (#4)"], MappingDesigner.Columns(h).Where(c => c.DisplayName.StartsWith("date")).Select(c => c.DisplayName));
        var spec = BuildSuggested("Orders", "orders_duplicate_headers.csv", a => a["Orders/Order/OrderDate"] = 3);
        var v = MappingDesigner.Verify(Xsd, spec, Text("orders_duplicate_headers.csv"));
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
        Assert.Contains("<OrderDate>2024-01-02</OrderDate>", v.Xml);
    }

    [Fact]
    public void Abbreviated_quantity_must_be_assigned_manually()
    {
        var h = Headers("orders_abbreviated.csv");
        Assert.False(MappingDesigner.Build(Doc, "Orders", h, Suggested("Orders", h)).Success);
        var spec = BuildSuggested("Orders", "orders_abbreviated.csv", a => a["Orders/Order/Line/Quantity"] = 4);
        Assert.True(MappingDesigner.Verify(Xsd, spec, Text("orders_abbreviated.csv")).IsValid);
    }

    [Fact]
    public void Bad_data_is_reported()
    {
        var spec = BuildSuggested("Orders", "orders_clean.csv");
        var v = MappingDesigner.Verify(Xsd, spec, Text("orders_bad_data.csv"));
        Assert.Contains(v.Problems, p => p.Contains("row 3") && p.Contains("sku"));
    }

    [Fact]
    public void Blank_header_column_is_displayed_and_not_suggested()
    {
        var h = Headers("products_blank_header.csv");
        Assert.Equal("(blank) (#3)", MappingDesigner.Columns(h)[2].DisplayName);
        Assert.DoesNotContain(2, Suggested("Products", h).Values);
    }

    [Fact]
    public void Reordered_file_fails_a_mapping_built_from_the_original()
    {
        var v = MappingDesigner.Verify(Xsd, BuildSuggested("Invoices", "invoices_clean.csv"), Text("invoices_reordered.csv"));
        Assert.Contains(v.Problems, p => p.Contains("expected header 'invoice_number', found 'order_id'"));
    }
}
