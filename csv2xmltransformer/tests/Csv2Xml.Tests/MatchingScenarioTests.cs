using Csv2Xml.Design;

namespace Csv2Xml.Tests;

/// <summary>Suggestion behaviour on realistic header variations. Expected column null = no suggestion (user must choose).</summary>
public class MatchingScenarioTests
{
    static readonly Design.Schema.SchemaDocument Doc = TestKit.Doc;

    static string? Suggested(string kind, string headerCsv, string path)
    {
        var h = headerCsv.Split(',');
        var c = MappingDesigner.Suggest(Doc, kind, h)[path];
        return c is null ? null : h[c.Value];
    }

    [Theory]
    [InlineData("Customers", "cutsomer_name", "Customers/Customer/CustomerName", "cutsomer_name")]       // transposition typo
    [InlineData("Customers", "Customer Name", "Customers/Customer/CustomerName", "Customer Name")]       // spaces
    [InlineData("Customers", "address_city", "Customers/Customer/Address/City", "address_city")]         // path-qualified
    [InlineData("Customers", "customer_address_city", "Customers/Customer/Address/City", "customer_address_city")]
    [InlineData("Customers", "Customer ID", "Customers/Customer/@id", "Customer ID")]                    // parent-qualified attribute
    [InlineData("Invoices", "invoice_total", "Invoices/Invoice/Total", "invoice_total")]                 // parent prefix
    [InlineData("Customers", "postcode", "Customers/Customer/Address/PostCode", "postcode")]
    [InlineData("Orders", "UnitPrice", "Orders/Order/Line/UnitPrice", "UnitPrice")]
    public void Fuzzy_variants_are_suggested(string kind, string headers, string path, string expected) =>
        Assert.Equal(expected, Suggested(kind, headers, path));

    [Theory]
    [InlineData("Orders", "foo,bar,baz", "Orders/Order/@orderId")]                      // unrelated headers
    [InlineData("Orders", "id,date", "Orders/Order/@orderId")]                          // too vague to claim
    [InlineData("Customers", ",,", "Customers/Customer/CustomerName")]                   // blank headers
    [InlineData("Products", "price", "Products/Product/@sku")]
    public void Weak_or_missing_headers_are_not_suggested(string kind, string headers, string path) =>
        Assert.Null(Suggested(kind, headers, path));

    /// <summary>Known limit: matching is lexical. Abbreviations and synonyms are left for the user to assign.</summary>
    [Theory]
    [InlineData("Orders", "Qty", "Orders/Order/Line/Quantity")]
    [InlineData("Invoices", "Invoice No", "Invoices/Invoice/InvoiceNumber")]
    [InlineData("Customers", "Town", "Customers/Customer/Address/City")]
    [InlineData("Customers", "Zip", "Customers/Customer/Address/PostCode")]
    public void Abbreviations_and_synonyms_are_not_guessed(string kind, string headers, string path) =>
        Assert.Null(Suggested(kind, headers, path));

    [Fact]
    public void Exact_match_beats_near_match_for_the_same_target()
    {
        Assert.Equal("Total", Suggested("Invoices", "Totals,Total", "Invoices/Invoice/Total"));
        Assert.Equal("email", Suggested("Customers", "e_mail_address,email", "Customers/Customer/Email"));
    }

    [Fact]
    public void Each_column_is_suggested_for_at_most_one_target()
    {
        var h = "id,order_id,customer_id,sku,quantity,unit_price,order_date".Split(',');
        var used = MappingDesigner.Suggest(Doc, "Orders", h).Values.OfType<int>().ToList();
        Assert.Equal(used.Count, used.Distinct().Count());
    }

    [Fact]
    public void Duplicate_headers_suggest_the_first_and_leave_the_other_free()
    {
        var s = MappingDesigner.Suggest(Doc, "Customers", "id,city,city".Split(','));
        Assert.Equal(1, s["Customers/Customer/Address/City"]);
        Assert.DoesNotContain(2, s.Values);
    }

    [Fact]
    public void Same_leaf_name_in_two_kinds_is_resolved_per_kind()
    {
        // 'sku' exists in Orders (Line/@sku) and Products (Product/@sku); 'order_id' in Orders and Invoices.
        Assert.Equal("sku", Suggested("Products", "sku,product_name", "Products/Product/@sku"));
        Assert.Equal("sku", Suggested("Orders", "order_id,sku", "Orders/Order/Line/@sku"));
        Assert.Equal("order_id", Suggested("Invoices", "invoice_number,order_id", "Invoices/Invoice/@orderId"));
    }

    [Theory]
    [InlineData("Customers", "ID,Customer Name,E-mail,Street,Town,City,Post Code,Notes")]
    [InlineData("Orders", "OrderID,CustomerID,Date of Order,SKU,Qty,Unit Price")]
    [InlineData("Products", "Product SKU,Name,Price (GBP),Discontinued?")]
    [InlineData("Invoices", "Invoice No,Order ID,Issued,Invoice Total")]
    public void Kind_is_classified_from_noisy_real_world_headers(string expected, string headers)
    {
        Assert.Equal(expected, MappingDesigner.RankKinds(Doc, headers.Split(','))[0].Kind);
    }
}
