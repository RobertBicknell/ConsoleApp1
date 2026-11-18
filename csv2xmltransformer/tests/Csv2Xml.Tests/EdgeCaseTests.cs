using System.Text;
using System.Xml.Linq;
using Csv2Xml.Design;
using Csv2Xml.Runtime.Csv;
using Csv2Xml.Runtime.Mapping;
using Csv2Xml.Runtime.Output;
using Csv2Xml.Runtime.Run;
using Csv2Xml.Runtime.Validation;

namespace Csv2Xml.Tests;

/// <summary>Input, encoding, schema and packaging edge cases. Each test pins a behaviour documented in README "Edge cases".</summary>
public class EdgeCaseTests
{
    static readonly MappingSpec Products = TestKit.Build(TestKit.Kind("Products"), ["sku", "product_name", "list_price"]);
    static VerifyResult V(string csv) => MappingDesigner.Verify(TestKit.XsdText, Products, csv);

    // ---------- CSV text ----------

    [Fact]
    public void Leading_bom_in_csv_text_is_ignored()
    {
        Assert.True(V("﻿sku,product_name,list_price\nA,B,1\n").IsValid);
        Assert.Equal(["sku", "a"], CsvParser.Read(new StringReader("﻿sku,a\n")).First());
    }

    [Theory]
    [InlineData("A,Smith, John,1", 4)] // unquoted delimiter inside a value
    [InlineData("A,B", 2)]             // field missing
    public void Row_with_wrong_field_count_is_a_row_error_not_a_column_shift(string row, int fields)
    {
        var r = V("sku,product_name,list_price\n" + row + "\n");
        Assert.Equal([$"row 2: has {fields} fields but the header has 3 (unquoted delimiter in a value, or a missing field?)"], r.Problems);
    }

    [Fact]
    public void Quoted_delimiters_quotes_and_newlines_are_data()
    {
        var r = V("sku,product_name,list_price\nA,\"Smith, \"\"J\"\"\nline2\",1\n");
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        Assert.Equal("Smith, \"J\"\nline2", XDocument.Parse(r.Xml).Descendants("ProductName").Single().Value);
    }

    [Fact]
    public void Unterminated_quote_halts_with_the_line_it_starts_on()
    {
        var r = V("sku,product_name,list_price\nA,B,1\nC,\"D,2\nE,F,3\n");
        Assert.Equal(["row 3: line 3: quoted field is never closed"], r.Problems);
    }

    [Fact]
    public void Blank_rows_are_skipped_and_counted()
    {
        var sb = new StringBuilder();
        using var xw = System.Xml.XmlWriter.Create(sb);
        var res = TestKit.Transform(Products, new StringReader("sku,product_name,list_price\nA,B,1\n,,\n  , ,\n\nC,D,2\n"), xw);
        Assert.True(res.Success);
        Assert.Equal((2L, 2L), (res.RowsWritten, res.BlankRows)); // the empty line is dropped by the parser, the others skipped
    }

    [Fact]
    public void Characters_illegal_in_xml_are_row_errors_not_crashes()
    {
        Assert.Equal(["row 2: column 2 ('Products/Product/ProductName') contains a character not allowed in XML (U+0001)"],
            V("sku,product_name,list_price\nA,B\u0001,1\n").Problems);
        Assert.True(V("sku,product_name,list_price\nA,Café \U0001F600 中,1\n").IsValid); // accents, emoji (surrogate pair), CJK
    }

    [Fact]
    public void Illegal_character_in_a_run_value_is_refused_before_output()
    {
        var r = MappingDesigner.Verify(TestKit.XsdText, Products, "sku,product_name,list_price\nA,B,1\n",
            new Dictionary<string, string> { ["Submission/SubmitterName"] = "x\u0002" });
        Assert.Equal(["run value 'Submission/SubmitterName' contains a character not allowed in XML (U+0002)"], r.Problems);
    }

    [Fact]
    public void Header_comparison_ignores_case_and_surrounding_whitespace_including_nbsp()
    {
        Assert.True(V(" SKU ,Product_Name,LIST_PRICE\nA,B,1\n").IsValid);
        Assert.True(Csv2Xml.Runtime.Writing.ColumnBinder.HeaderEquals("﻿sku", "SKU")); // header captured by a host that kept the BOM
    }

    // ---------- encodings ----------

    static string TempFile(byte[] bytes) { var p = Path.GetTempFileName(); File.WriteAllBytes(p, bytes); return p; }

    static string ReadAll(string path, string? enc = null) { using var r = CsvParser.Open(path, enc); return r.ReadToEnd(); }

    [Fact]
    public void Non_utf8_bytes_fail_loudly_by_default_and_decode_with_a_declared_encoding()
    {
        var path = TempFile(Encoding.Latin1.GetBytes("sku,product_name,list_price\nA,Café,1\n"));
        try
        {
            var ex = Assert.ThrowsAny<DecoderFallbackException>(() => ReadAll(path));
            Assert.Contains("windows-1252", CsvParser.DescribeInputError(ex));
            Assert.Contains("Café", ReadAll(path, "windows-1252"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public void Byte_order_mark_selects_the_encoding(string kind)
    {
        Encoding enc = kind switch { "utf-8-bom" => new UTF8Encoding(true), "utf-16le" => new UnicodeEncoding(false, true), _ => new UnicodeEncoding(true, true) };
        var path = TempFile([.. enc.GetPreamble(), .. enc.GetBytes("sku,name\nA,Café\n")]);
        try
        {
            using var s = File.OpenRead(path);
            Assert.Equal(["sku", "name"], MappingDesigner.ReadHeaders(s, ',', encoding: "windows-1252")); // BOM wins over declared
            Assert.Contains("Café", ReadAll(path, "windows-1252"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("a,b,c", ',')]
    [InlineData("a;b;c", ';')]
    [InlineData("a\tb\tc", '\t')]
    [InlineData("a|b|c", '|')]
    [InlineData("\"x;y\",b,c", ',')] // delimiters inside quotes don't count
    [InlineData("single", ',')]
    public void Delimiter_is_detected_from_the_header_line(string line, char expected) =>
        Assert.Equal(expected, MappingDesigner.DetectDelimiter(line));

    // ---------- XSD ----------

    const string Unqualified = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:u" xmlns="urn:u">
          <xs:element name="Root"><xs:complexType><xs:sequence>
            <xs:element name="Batch" type="xs:string"/>
            <xs:choice>
              <xs:element name="Items"><xs:complexType><xs:sequence>
                <xs:element name="Item" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                  <xs:element name="Name" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Other" type="xs:string"/>
            </xs:choice>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    [Fact]
    public void Target_namespace_with_unqualified_local_elements_produces_valid_xml()
    {
        var doc = MappingDesigner.LoadSchema(Unqualified);
        Assert.Equal(("urn:u", ""), (doc.Namespace, doc.LocalNamespace));
        var spec = MappingDesigner.Build(doc, "Items", ["name"], new Dictionary<string, int> { ["Items/Item/Name"] = 0 }).Spec!;
        var r = MappingDesigner.Verify(Unqualified, spec, "name\nA\n", new Dictionary<string, string> { ["Root/Batch"] = "b" });
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        var x = XDocument.Parse(r.Xml).Root!;
        Assert.Equal("{urn:u}Root", x.Name.ToString());
        Assert.Equal("Batch", x.Elements().First().Name.ToString()); // no namespace
        Assert.Equal("Item", x.Element("Items")!.Elements().Single().Name.ToString());
    }

    // Helper global elements declared before the root: one plain, one with its own choice; both used via ref inside the root.
    const string HelperGlobals = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">
          <xs:element name="Contact" type="xs:string"/>
          <xs:element name="Channel"><xs:complexType><xs:choice>
            <xs:element name="Email" type="xs:string"/>
            <xs:element name="Phone" type="xs:string"/>
          </xs:choice></xs:complexType></xs:element>
          <xs:element name="Submission"><xs:complexType><xs:sequence>
            <xs:element name="SubmitterName" type="xs:string"/>
            <xs:element ref="Contact"/>
            <xs:element ref="Channel" minOccurs="0"/>
            <xs:choice>
              <xs:element name="Orders"><xs:complexType><xs:sequence>
                <xs:element name="Order" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                  <xs:element name="Id" type="xs:int"/>
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Products"><xs:complexType><xs:sequence>
                <xs:element name="Product" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                  <xs:element name="Sku" type="xs:string"/>
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:choice>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    [Fact]
    public void Kinds_come_from_the_first_root_with_a_choice_not_from_helper_globals()
    {
        var doc = MappingDesigner.LoadSchema(HelperGlobals);
        Assert.Equal("Submission", doc.RootName);
        Assert.Equal(["Orders", "Products"], MappingDesigner.Kinds(doc));
        Assert.Contains(doc.RootTargets, t => t.Path == "Submission/SubmitterName");

        var spec = MappingDesigner.Build(doc, "Products", ["sku"], new Dictionary<string, int> { ["Products/Product/Sku"] = 0 }).Spec!;
        var r = MappingDesigner.Verify(HelperGlobals, spec, "sku\nA\nB\n");
        Assert.True(r.IsValid, string.Join("; ", r.Problems));
        var x = XDocument.Parse(r.Xml).Root!;
        Assert.Equal("Submission", x.Name.LocalName);
        Assert.Equal(2, x.Element("Products")!.Elements("Product").Count()); // the chosen branch is written by the tool
    }

    [Fact]
    public void Qualified_local_elements_share_the_target_namespace()
    {
        var doc = MappingDesigner.LoadSchema(FacetTests.RestrictedXsd);
        Assert.Equal(("urn:example:shipping", "urn:example:shipping"), (doc.Namespace, doc.LocalNamespace));
    }

    [Fact]
    public void Mixed_element_forms_are_refused_clearly()
    {
        var mixed = Unqualified.Replace("<xs:element name=\"Batch\" type=\"xs:string\"/>", "<xs:element name=\"Batch\" type=\"xs:string\" form=\"qualified\"/>");
        var ex = Assert.Throws<InvalidDataException>(() => MappingDesigner.LoadSchema(mixed));
        Assert.Contains("mixes qualified and unqualified", ex.Message);
    }

    [Theory]
    [InlineData("<xs:include schemaLocation='common.xsd'/>")]
    [InlineData("<xs:import namespace='urn:other' schemaLocation='other.xsd'/>")]
    public void Includes_and_imports_are_refused_not_silently_ignored(string directive)
    {
        var xsd = $"<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'>{directive}<xs:element name='A' type='xs:string'/></xs:schema>";
        Assert.Contains("not supported", Assert.Throws<InvalidDataException>(() => MappingDesigner.LoadSchema(xsd)).Message);
        Assert.ThrowsAny<System.Xml.Schema.XmlSchemaException>(() => XsdValidator.Compile(xsd));
    }

    [Fact]
    public void Xsd_with_a_dtd_is_refused()
    {
        var xsd = "<!DOCTYPE x [<!ENTITY e 'v'>]><xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:element name='A' type='xs:string'/></xs:schema>";
        Assert.Throws<InvalidDataException>(() => MappingDesigner.LoadSchema(xsd));
    }

    const string Siblings = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:element name="Order"><xs:complexType><xs:sequence>
            <xs:element name="Ref" type="xs:string"/>
            <xs:element name="Line" maxOccurs="unbounded" type="xs:string"/>
            <xs:element name="Note" minOccurs="0" maxOccurs="unbounded" type="xs:string"/>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;

    [Fact]
    public void One_of_two_sibling_repeating_groups_can_be_mapped()
    {
        var doc = MappingDesigner.LoadSchema(Siblings);
        var r = MappingDesigner.Build(doc, "Order", ["ref", "line"], new Dictionary<string, int> { ["Order/Ref"] = 0, ["Order/Line"] = 1 });
        Assert.True(r.Success, string.Join("; ", r.Errors));
        var v = MappingDesigner.Verify(Siblings, r.Spec!, "ref,line\nR1,a\nR1,b\n");
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
        Assert.Equal(2, XDocument.Parse(v.Xml).Descendants("Line").Count());
    }

    [Fact]
    public void Mapping_both_sibling_repeating_groups_is_a_build_error()
    {
        var doc = MappingDesigner.LoadSchema(Siblings);
        var r = MappingDesigner.Build(doc, "Order", ["ref", "line", "note"],
            new Dictionary<string, int> { ["Order/Ref"] = 0, ["Order/Line"] = 1, ["Order/Note"] = 2 });
        Assert.Contains(r.Errors, e => e.Contains("different repeating group"));
    }

    [Fact]
    public void Generator_demonstrates_the_chain_and_notes_the_optional_sibling()
    {
        var s = Csv2Xml.Design.Samples.SampleGenerator.Generate(Siblings);
        Assert.True(s.AllPassed, s.Readme());
        Assert.Contains(s.Notes, n => n.Contains("'Order/Note' not demonstrated"));
    }

    [Fact]
    public void Malformed_exemplar_is_reported_clearly() =>
        Assert.StartsWith("exemplar XML is not well-formed",
            Assert.Throws<InvalidDataException>(() => Csv2Xml.Design.Samples.SampleGenerator.Generate(TestKit.XsdText, "<Submission>")).Message);

    // ---------- stored config ----------

    [Theory]
    [InlineData("{\"format\":\"csv2xml.mapping\",\"version\":1,\"content\":{\"kind\":\"P\",\"hasHeader\":true,\"delimiter\":\",\"}}")] // no fields
    [InlineData("{\"format\":\"csv2xml.mapping\",\"version\":1,\"content\":{\"kind\":null,\"hasHeader\":true,\"delimiter\":\",\",\"fields\":[]}}")]
    public void Structurally_incomplete_config_is_rejected_on_load(string json) =>
        Assert.Throws<InvalidDataException>(() => ConfigJson.MappingFromJson(json));

    [Fact]
    public void Config_round_trip_preserves_namespaces_and_encoding()
    {
        var doc = MappingDesigner.LoadSchema(Unqualified);
        var spec = MappingDesigner.Build(doc, "Items", ["name"], new Dictionary<string, int> { ["Items/Item/Name"] = 0 }, ';', "windows-1252").Spec!;
        var back = ConfigJson.MappingFromBytes(ConfigJson.ToBytes(spec));
        Assert.Equal((spec.RootNamespace, spec.Namespace, spec.LocalNamespace, spec.Encoding, spec.Delimiter),
            (back.RootNamespace, back.Namespace, back.LocalNamespace, back.Encoding, back.Delimiter));
    }

    // ---------- delivery ----------

    sealed class FailingStream : MemoryStream
    {
        public FailingStream() : base(new byte[100_000]) { }
        public override int Read(byte[] buffer, int offset, int count) => Position > 10_000 ? throw new IOException("disk gone") : base.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Position > 10_000 ? throw new IOException("disk gone") : base.ReadAsync(buffer, ct);
    }

    [Fact]
    public async Task File_sink_leaves_no_partial_file_when_copy_fails()
    {
        var dir = Directory.CreateTempSubdirectory("c2x-sink-").FullName;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new FileOutputSink(dir).SendAsync(new OutputPackage("p.zip", () => new FailingStream(), 100_000)));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Job_with_two_mappings_for_one_kind_is_refused()
    {
        var job = MappingDesigner.Job("J", [Products, Products]);
        var ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(Path.GetTempPath()))
            .RunAsync(job, new RunRequest(new Dictionary<string, string>(), new Dictionary<string, string> { ["submissionId"] = "1", ["SubmitterName"] = "n" })));
        Assert.Contains("job has 2 mappings for kind 'Products'", ex.Problems);
    }
}
