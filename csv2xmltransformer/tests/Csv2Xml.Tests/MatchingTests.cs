using Csv2Xml.Design.Matching;
using Csv2Xml.Design.Schema;

namespace Csv2Xml.Tests;

public class MatchingTests
{
    static IReadOnlyList<SchemaKind> Kinds => TestKit.Doc.Kinds;

    [Fact]
    public void Reads_four_kinds_from_choice()
    {
        Assert.Equal(["Customers", "Orders", "Products", "Invoices"], Kinds.Select(k => k.Name));
    }

    [Fact]
    public void Flattens_paths_attributes_and_requiredness()
    {
        var orders = Kinds.Single(k => k.Name == "Orders");
        var sku = orders.Targets.Single(t => t.Path == "Orders/Order/Line/@sku");
        Assert.Equal(TargetKind.Attribute, sku.Kind);
        Assert.True(sku.Required);
        Assert.Equal(["Orders/Order", "Orders/Order/Line"], sku.RepeatingAncestors);

        var customers = Kinds.Single(k => k.Name == "Customers");
        Assert.False(customers.Targets.Single(t => t.Name == "Email").Required);
        Assert.False(customers.Targets.Single(t => t.Name == "City").Required); // under optional Address
    }

    [Theory]
    [InlineData("customer_name", "CustomerName")]
    [InlineData("CUSTOMERNAME", "CustomerName")]
    [InlineData("Customer Name", "CustomerName")]
    [InlineData("customername_", "CustomerName")]
    [InlineData("order_id", "orderId")]
    public void Tolerates_case_and_underscores(string header, string leaf)
    {
        Assert.Equal(1.0, NameMatcher.Similarity(header, leaf));
    }

    [Fact]
    public void Suggests_despite_order_noise_and_extra_columns()
    {
        string[] headers = ["junk", "unit_price", "Order_Id", "sku", "customer_id", "quantity", "OrderDate", "notes"];
        var m = MappingSuggester.Evaluate(headers, Kinds.Single(k => k.Name == "Orders"));
        string? H(string path) => m.Suggestions.Single(s => s.Target.Path == path).Header;
        Assert.Equal("unit_price", H("Orders/Order/Line/UnitPrice"));
        Assert.Equal("Order_Id", H("Orders/Order/@orderId"));
        Assert.Equal("customer_id", H("Orders/Order/@customerId"));
        Assert.Equal("sku", H("Orders/Order/Line/@sku"));
        Assert.Equal("OrderDate", H("Orders/Order/OrderDate"));
        Assert.Equal(6, m.Suggestions.Count(s => s.ColumnIndex is not null));
    }

    [Theory]
    [InlineData("Customers", "id,customer_name,email,street,city,post_code")]
    [InlineData("Orders", "order_id,customer_id,order_date,sku,quantity,unit_price")]
    [InlineData("Products", "sku,product_name,list_price,discontinued")]
    [InlineData("Invoices", "invoice_number,order_id,issued_on,total")]
    public void Classifies_kind_from_headers(string expected, string csvHeader)
    {
        var ranked = MappingSuggester.Classify(csvHeader.Split(','), Kinds);
        Assert.Equal(expected, ranked[0].Kind.Name);
        Assert.True(ranked[0].Score > ranked[1].Score);
    }
}
