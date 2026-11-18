using System.Text;
using System.Xml;
using System.Xml.Linq;
using Csv2Xml.Design;
using Csv2Xml.Runtime.Mapping;

namespace Csv2Xml.Tests;

public class DesignerTests
{
    static readonly Design.Schema.SchemaDocument Doc = MappingDesigner.LoadSchema(TestKit.XsdText);

    [Fact]
    public void Columns_suffix_only_ambiguous_and_blank_headers()
    {
        var c = MappingDesigner.Columns(["id", "Name", "email", "name ", "", "Name"]);
        Assert.Equal(["id", "Name (#2)", "email", "name (#4)", "(blank) (#5)", "Name (#6)"], c.Select(x => x.DisplayName));
        Assert.Equal([0, 1, 2, 3, 4, 5], c.Select(x => x.Index));
    }

    [Fact]
    public void Columns_resolve_clash_between_literal_and_generated_names()
    {
        var c = MappingDesigner.Columns(["A", "A", "A (#2)"]);
        Assert.Equal(3, c.Select(x => x.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Targets_exclude_run_values()
    {
        var t = MappingDesigner.Targets(Doc, "Customers").Select(x => x.Path).ToList();
        Assert.Contains("Customers/Customer/@id", t);
        Assert.DoesNotContain(t, p => p.StartsWith("Submission"));
    }

    [Fact]
    public void Suggest_then_build_round_trips_and_transforms()
    {
        string[] h = ["id", "customer_name", "email", "street", "city", "post_code"];
        var assign = MappingDesigner.Suggest(Doc, "Customers", h).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        var r = MappingDesigner.Build(Doc, "Customers", h, assign);
        Assert.True(r.Success, string.Join("; ", r.Errors));
        Assert.Equal(Doc.XsdHash, r.Spec!.XsdHash); // identified by content, not location

        var spec = ConfigJson.MappingFromBytes(ConfigJson.ToBytes(r.Spec));
        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb)) TestKit.Transform(spec, new StringReader(string.Join(',', h) + "\n7,Ann,a@x,1 Main,Leeds,LS1\n"), xw);
        Assert.Equal("Leeds", (string?)XDocument.Parse(sb.ToString()).Descendants("City").Single());
    }

    [Fact]
    public void Duplicate_headers_are_assigned_by_index()
    {
        string[] h = ["sku", "name", "name", "price"];
        var r = MappingDesigner.Build(Doc, "Products", h, new Dictionary<string, int>
        {
            ["Products/Product/@sku"] = 0, ["Products/Product/ProductName"] = 2, ["Products/Product/ListPrice"] = 3,
        });
        Assert.True(r.Success);
        var f = r.Spec!.Fields.Single(x => x.Path == "Products/Product/ProductName");
        Assert.Equal(2, f.ColumnIndex);
        Assert.Equal("name", f.Column);
    }

    [Fact]
    public void Build_reports_all_errors_together()
    {
        var r = MappingDesigner.Build(Doc, "Products", ["sku", "name"], new Dictionary<string, int>
        {
            ["Products/Product/@sku"] = 0,
            ["Products/Product/ProductName"] = 5,
            ["Products/Product/Nope"] = 1,
            ["Submission/SubmitterName"] = 1,
        });
        Assert.False(r.Success);
        Assert.Null(r.Spec);
        Assert.Contains("unknown target 'Products/Product/Nope'", r.Errors);
        Assert.Contains(r.Errors, e => e.Contains("Submission/SubmitterName") && e.Contains("run value"));
        Assert.Contains(r.Errors, e => e.Contains("ProductName") && e.Contains("column 6"));
        Assert.Contains("required 'Products/Product/ListPrice' has no column", r.Errors);
    }

    [Fact]
    public void Shared_column_is_a_warning_not_an_error()
    {
        string[] h = ["sku", "name", "price"];
        var r = MappingDesigner.Build(Doc, "Products", h, new Dictionary<string, int>
        {
            ["Products/Product/@sku"] = 0, ["Products/Product/ProductName"] = 0, ["Products/Product/ListPrice"] = 2,
        });
        Assert.True(r.Success);
        Assert.Contains(r.Warnings, w => w.Contains("'sku' feeds 2 targets"));
    }

    static MappingSpec OrdersSpec()
    {
        var h = "order_id,customer_id,order_date,sku,quantity,unit_price".Split(',');
        var a = MappingDesigner.Suggest(Doc, "Orders", h).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        return MappingDesigner.Build(Doc, "Orders", h, a).Spec!;
    }

    [Fact]
    public void Verify_accepts_conforming_sample_with_placeholder_run_values()
    {
        var r = MappingDesigner.Verify(TestKit.XsdText, OrdersSpec(),
            "order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,2,1.50\n10,1,2024-01-02,B,1,9.99\n");
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        Assert.Equal(2, XDocument.Parse(r.Xml).Descendants("Line").Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Verify_preview_follows_the_declaration_setting(bool omit)
    {
        var r = MappingDesigner.Verify(TestKit.XsdText, OrdersSpec(),
            "order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,2,1.50\n", omitXmlDeclaration: omit);
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        Assert.Equal(!omit, r.Xml.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
    }

    [Fact]
    public void Verify_reports_xsd_type_violation()
    {
        var r = MappingDesigner.Verify(TestKit.XsdText, OrdersSpec(),
            "order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,many,1.50\n");
        Assert.Contains(r.Problems, p => p.StartsWith("XSD") && p.Contains("Quantity"));
    }

    [Fact]
    public void Verify_reports_changed_layout_without_throwing()
    {
        var r = MappingDesigner.Verify(TestKit.XsdText, OrdersSpec(),
            "customer_id,order_id,order_date,sku,quantity,unit_price\n1,10,2024-01-02,A,2,1.50\n");
        Assert.False(r.IsValid);
        Assert.Contains(r.Problems, p => p.Contains("expected header 'order_id'"));
    }

    [Fact]
    public void Unknown_kind_is_an_error_and_kinds_rank_by_headers()
    {
        Assert.Equal(["unknown kind 'X'"], MappingDesigner.Build(Doc, "X", ["a"], new Dictionary<string, int>()).Errors);
        Assert.Equal("Orders", MappingDesigner.RankKinds(Doc, "order_id,customer_id,order_date,sku,quantity,unit_price".Split(','))[0].Kind);
    }
}
