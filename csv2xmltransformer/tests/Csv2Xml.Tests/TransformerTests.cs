using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Csv2Xml.Design.Matching;
using Csv2Xml.Design.Schema;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Tests;

public class TransformerTests
{
    static SchemaKind Kind(string n) => TestKit.Kind(n);
    static readonly XmlSchemaSet Schemas = Csv2Xml.Runtime.Validation.XsdValidator.Compile(TestKit.XsdText);

    static (XDocument doc, TransformResult res, string xml) Run(string kind, string csv, ErrorPolicy p = ErrorPolicy.Halt, bool validate = true)
    {
        var k = Kind(kind);
        var first = csv.Split('\n')[0].TrimEnd('\r').Split(',');
        var spec = TestKit.Build(k, first);
        var sb = new StringBuilder();
        TransformResult res;
        using (var xw = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
            res = TestKit.Transform(spec, new StringReader(csv), xw, p);
        var xml = sb.ToString();
        var doc = XDocument.Parse(xml);
        if (validate && res.Success)
        {
            var errs = new List<string>();
            doc.Validate(Schemas, (_, e) => errs.Add(e.Message));
            Assert.Empty(errs);
        }
        return (doc, res, xml);
    }

    [Fact]
    public void Flat_customers_with_nested_optional_address_validate_against_xsd()
    {
        var (doc, res, _) = Run("Customers",
            "id,customer_name,email,street,city,post_code\n1,Ann,a@x.com,1 Main,Leeds,LS1\n2,Bob,,,,\n");
        Assert.True(res.Success);
        var cs = doc.Root!.Element("Customers")!.Elements("Customer").ToList();
        Assert.Equal(2, cs.Count);
        Assert.Equal("1", (string?)cs[0].Attribute("id"));
        Assert.Equal("Leeds", (string?)cs[0].Element("Address")!.Element("City"));
        Assert.Null(cs[1].Element("Email"));    // empty optional omitted
        Assert.Null(cs[1].Element("Address"));  // empty optional group omitted
    }

    [Fact]
    public void Hierarchy_groups_contiguous_rows_into_orders_with_lines()
    {
        var (doc, res, _) = Run("Orders",
            "order_id,customer_id,order_date,sku,quantity,unit_price\n" +
            "10,1,2024-01-02,A,2,1.50\n10,1,2024-01-02,B,1,9.99\n11,2,2024-01-03,A,5,1.50\n");
        Assert.True(res.Success);
        var orders = doc.Root!.Element("Orders")!.Elements("Order").ToList();
        Assert.Equal(2, orders.Count);
        Assert.Equal(["A", "B"], orders[0].Elements("Line").Select(l => (string?)l.Attribute("sku")));
        Assert.Single(orders[1].Elements("Line"));
        Assert.Equal(3, res.RowsWritten);
    }

    [Fact]
    public void Column_order_and_extra_columns_do_not_matter()
    {
        var (doc, _, _) = Run("Products", "zzz,list_price,SKU,Product_Name\nq,9.5,P1,Widget\n");
        var p = doc.Root!.Element("Products")!.Element("Product")!;
        Assert.Equal("P1", (string?)p.Attribute("sku"));
        Assert.Equal("Widget", (string?)p.Element("ProductName"));
    }

    [Fact]
    public void Missing_required_column_halts_before_any_output()
    {
        var k = Kind("Invoices");
        var hdr = new[] { "invoice_number", "issued_on", "total" }; // no orderId
        var spec = TestKit.Build(k, hdr);
        var sb = new StringBuilder();
        using var xw = XmlWriter.Create(sb);
        var ex = Assert.Throws<MappingException>(() =>
            TestKit.Transform(spec, new StringReader("invoice_number,issued_on,total\nI1,2024-01-01,5\n"), xw));
        Assert.Contains("Invoices/Invoice/@orderId", ex.Problems.Single());
        Assert.Equal("", sb.ToString());
    }

    [Fact]
    public void Missing_required_value_halts_or_skips_by_policy()
    {
        const string csv = "sku,product_name,list_price\nA,One,1\nB,,2\nC,Three,3\n";
        var (_, halt, _) = Run("Products", csv, ErrorPolicy.Halt, validate: false);
        Assert.True(halt.Halted);
        Assert.Equal(3, halt.Errors.Single().Row);

        var (doc, skip, _) = Run("Products", csv, ErrorPolicy.SkipRow);
        Assert.False(skip.Halted);
        Assert.Single(skip.Errors);
        Assert.Equal(["A", "C"], doc.Root!.Element("Products")!.Elements("Product").Select(p => (string?)p.Attribute("sku")));
    }

    [Fact]
    public void Headerless_binds_by_index()
    {
        var k = Kind("Products");
        var byPath = new Dictionary<string, int> { ["Products/Product/@sku"] = 0, ["Products/Product/ProductName"] = 1, ["Products/Product/ListPrice"] = 2 };
        var spec = SpecBuilder.Build(TestKit.Doc, k, byPath, headers: null, ';');
        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb)) TestKit.Transform(spec, new StringReader("P1;Widget;2.5\nP2;Gadget;3\n"), xw);
        var prods = XDocument.Parse(sb.ToString()).Root!.Element("Products")!.Elements("Product").ToList();
        Assert.Equal(["P1", "P2"], prods.Select(p => (string?)p.Attribute("sku")));
    }

    [Fact]
    public void Special_characters_are_escaped()
    {
        var (doc, _, xml) = Run("Products", "sku,product_name,list_price\nA,\"R&D <x> \"\"q\"\"\",1\n");
        Assert.Equal("R&D <x> \"q\"", (string?)doc.Root!.Descendants("ProductName").Single());
        Assert.Contains("R&amp;D", xml);
    }

    [Fact]
    public void Zero_rows_yields_empty_kind_root()
    {
        var (doc, res, _) = Run("Products", "sku,product_name,list_price\n", validate: false);
        Assert.True(res.Success);
        Assert.Empty(doc.Root!.Element("Products")!.Elements());
    }

    [Fact]
    public void Large_input_streams_in_bounded_memory()
    {
        var k = Kind("Orders");
        var hdr = "order_id,customer_id,order_date,sku,quantity,unit_price".Split(',');
        var spec = TestKit.Build(k, hdr);
        IEnumerable<string> Lines()
        {
            yield return string.Join(',', hdr) + "\n";
            for (var i = 0; i < 300_000; i++) yield return $"{i / 3},1,2024-01-01,S{i % 3},1,1.00\n";
        }
        var before = GC.GetTotalMemory(true);
        long peak = 0;
        using var xw = XmlWriter.Create(Stream.Null);
        var res = TestKit.Transform(spec, new LazyReader(Lines(), () => peak = Math.Max(peak, GC.GetTotalMemory(false))), xw);
        Assert.Equal(300_000, res.RowsWritten);
        Assert.True(peak - before < 50_000_000, $"retained {peak - before} bytes");
    }

    sealed class LazyReader(IEnumerable<string> lines, Action tick) : TextReader
    {
        readonly IEnumerator<string> e = lines.GetEnumerator(); string cur = ""; int pos; int n;
        public override int Read(char[] buffer, int index, int count)
        {
            while (pos >= cur.Length) { if (!e.MoveNext()) return 0; cur = e.Current; pos = 0; if (++n % 20000 == 0) tick(); }
            var len = Math.Min(count, cur.Length - pos); cur.CopyTo(pos, buffer, index, len); pos += len; return len;
        }
    }
}
