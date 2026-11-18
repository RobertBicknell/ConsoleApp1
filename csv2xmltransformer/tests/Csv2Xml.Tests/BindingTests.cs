using System.Text;
using System.Xml;
using System.Xml.Linq;
using Csv2Xml.Design.Matching;
using Csv2Xml.Design.Schema;
using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Tests;

/// <summary>Binding is by column index; the header recorded at design time is verified at run time.</summary>
public class BindingTests
{
    static SchemaKind Products => TestKit.Doc.Kinds.Single(k => k.Name == "Products");

    /// <summary>Spec from explicit path -> column index assignments against a design-time header row.</summary>
    static MappingSpec Spec(string[] header, params (string path, int col)[] map)
    {
        var byPath = map.ToDictionary(m => m.path, m => m.col);
        return SpecBuilder.Build(TestKit.Doc, Products, byPath, header);
    }

    static (XDocument doc, TransformResult res) Run(MappingSpec spec, string csv)
    {
        var sb = new StringBuilder();
        TransformResult res;
        using (var xw = XmlWriter.Create(sb)) res = TestKit.Transform(spec, new StringReader(csv), xw);
        return (XDocument.Parse(sb.ToString()), res);
    }

    static MappingException Fails(MappingSpec spec, string csv)
    {
        var sb = new StringBuilder();
        using var xw = XmlWriter.Create(sb);
        var ex = Assert.Throws<MappingException>(() => TestKit.Transform(spec, new StringReader(csv), xw));
        Assert.Equal("", sb.ToString()); // nothing written
        return ex;
    }

    [Fact]
    public void Duplicate_headers_bind_to_the_chosen_column_not_the_first()
    {
        string[] h = ["sku", "name", "name", "list_price"];
        var spec = Spec(h, ("Products/Product/@sku", 0), ("Products/Product/ProductName", 2), ("Products/Product/ListPrice", 3));
        var (doc, res) = Run(spec, "sku,name,name,list_price\nP1,short,Long Name,2.5\n");
        Assert.True(res.Success);
        Assert.Equal("Long Name", (string?)doc.Descendants("ProductName").Single());
    }

    [Fact]
    public void Reordered_columns_fail_before_output_naming_column_and_headers()
    {
        string[] h = ["sku", "product_name", "list_price"];
        var spec = Spec(h, ("Products/Product/@sku", 0), ("Products/Product/ProductName", 1), ("Products/Product/ListPrice", 2));
        var ex = Fails(spec, "sku,list_price,product_name\nP1,2.5,Widget\n");
        Assert.Contains("column 2 expected header 'product_name', found 'list_price'", string.Join("|", ex.Problems));
        Assert.Contains("column 3 expected header 'list_price', found 'product_name'", string.Join("|", ex.Problems));
    }

    [Fact]
    public void Inserted_column_fails_even_for_optional_fields()
    {
        string[] h = ["sku", "product_name", "list_price", "discontinued"];
        var spec = Spec(h, ("Products/Product/@sku", 0), ("Products/Product/ProductName", 1), ("Products/Product/ListPrice", 2),
            ("Products/Product/Discontinued", 3));
        var ex = Fails(spec, "sku,extra,product_name,list_price,discontinued\nP1,x,W,1,false\n");
        Assert.Contains(ex.Problems, p => p.Contains("Products/Product/Discontinued"));
    }

    [Fact]
    public void Header_check_ignores_case_and_surrounding_spaces()
    {
        string[] h = ["sku", "product_name", "list_price"];
        var spec = Spec(h, ("Products/Product/@sku", 0), ("Products/Product/ProductName", 1), ("Products/Product/ListPrice", 2));
        var (_, res) = Run(spec, " SKU ,Product_Name,LIST_PRICE\nP1,W,1\n");
        Assert.True(res.Success);
    }

    [Fact]
    public void Header_shorter_than_mapping_fails()
    {
        string[] h = ["sku", "product_name", "list_price"];
        var spec = Spec(h, ("Products/Product/@sku", 0), ("Products/Product/ProductName", 1), ("Products/Product/ListPrice", 2));
        var ex = Fails(spec, "sku,product_name\nP1,W\n");
        Assert.Contains(ex.Problems, p => p.Contains("column 3") && p.Contains("only 2 columns"));
    }

    [Fact]
    public void Unmapped_required_field_fails()
    {
        string[] h = ["sku", "product_name", "list_price"];
        var spec = Spec(h, ("Products/Product/@sku", 0));
        var ex = Fails(spec, "sku,product_name,list_price\nP1,W,1\n");
        Assert.Contains("required 'Products/Product/ProductName' is not mapped", ex.Problems);
    }
}
