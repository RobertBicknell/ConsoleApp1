using System.Text;
using System.Xml;
using Csv2Xml.Design;
using Csv2Xml.Runtime.Validation;

namespace Csv2Xml.Tests;

public class ValidationTests
{
    static readonly System.Xml.Schema.XmlSchemaSet Schemas = XsdValidator.Compile(TestKit.XsdText);

    static string Generate(string kind, string csv)
    {
        var spec = TestKit.Build(TestKit.Kind(kind), csv.Split('\n')[0].Split(','));
        var sb = new StringBuilder();
        using (var xw = XmlWriter.Create(sb)) TestKit.Transform(spec, new StringReader(csv), xw);
        return sb.ToString();
    }

    static ValidationReport Check(string xml) => XsdValidator.Validate(xml, Schemas);

    [Fact]
    public void Conforming_output_is_valid() =>
        Assert.True(Check(Generate("Orders", "order_id,customer_id,order_date,sku,quantity,unit_price\n1,2,2024-01-01,A,3,1.5\n")).IsValid);

    [Fact]
    public void Wrong_datatype_is_reported_with_position()
    {
        var r = Check(Generate("Orders", "order_id,customer_id,order_date,sku,quantity,unit_price\n1,2,2024-01-01,A,three,1.5\n"));
        Assert.False(r.IsValid);
        Assert.Contains(r.Issues, i => i.Message.Contains("Quantity") && i.Line >= 1);
    }

    [Fact]
    public void Bad_date_and_bad_attribute_are_both_reported()
    {
        var r = Check(Generate("Orders", "order_id,customer_id,order_date,sku,quantity,unit_price\nx,2,not-a-date,A,1,1\n"));
        Assert.True(r.Issues.Count >= 2);
    }

    [Fact]
    public void Issue_list_is_capped()
    {
        var rows = string.Concat(Enumerable.Range(0, 50).Select(_ => "x,y,z,A,q,w\n"));
        var r = XsdValidator.Validate(Generate("Orders", "order_id,customer_id,order_date,sku,quantity,unit_price\n" + rows), Schemas, maxIssues: 10);
        Assert.Equal(10, r.Issues.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Malformed_xml_is_reported_not_thrown()
    {
        var r = Check("<Submission><Orders>");
        Assert.Contains(r.Issues, i => i.Message.StartsWith("not well-formed"));
    }

    [Fact]
    public void Xsd_hash_ignores_bom_and_line_endings_but_not_content()
    {
        var lf = TestKit.XsdText.Replace("\r\n", "\n");
        Assert.Equal(XsdValidator.Hash(lf), XsdValidator.Hash("﻿" + lf.Replace("\n", "\r\n")));
        Assert.NotEqual(XsdValidator.Hash(lf), XsdValidator.Hash(lf + "<!-- changed -->"));
    }

    [Fact]
    public void Spec_records_xsd_hash_and_verify_refuses_a_changed_xsd()
    {
        const string csv = "sku,product_name,list_price\nA,B,1\n";
        var spec = TestKit.Build(TestKit.Kind("Products"), ["sku", "product_name", "list_price"]);
        Assert.Equal(XsdValidator.Hash(TestKit.XsdText), spec.XsdHash);
        Assert.True(MappingDesigner.Verify(TestKit.XsdText, spec, csv).IsValid);

        var changed = MappingDesigner.Verify(TestKit.XsdText + "<!-- changed -->", spec, csv);
        Assert.Equal(["mapping was designed against a different XSD; rebuild the mapping"], changed.Problems);
    }
}
