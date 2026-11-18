using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using Csv2Xml.Design.Matching;
using Csv2Xml.Runtime.Output;
using Csv2Xml.Runtime.Run;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Tests;

public class RunParameterTests
{
    static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 11, 15, 10, 30, 0, TimeSpan.Zero));
    sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Fact]
    public void Root_targets_are_separated_from_kinds_and_excluded_from_mapping()
    {
        Assert.Equal(new[] { "Submission/@submissionId", "Submission/SubmitterName", "Submission/SubmittedAt" }.Order(), TestKit.Doc.RootTargets.Select(t => t.Path).Order());
        Assert.All(TestKit.Doc.Kinds, k => Assert.DoesNotContain(k.Targets, t => t.Path.StartsWith("Submission/")));
        // a CSV column named like a root value must not map to anything
        var k = TestKit.Doc.Kinds.Single(x => x.Name == "Products");
        var sug = MappingSuggester.Suggest(["sku", "product_name", "list_price", "submission_id", "submitter_name"], k);
        Assert.DoesNotContain(sug, s => s.Header is "submission_id" or "submitter_name");
    }

    [Fact]
    public void Default_definitions_derive_from_root_targets()
    {
        var defs = SpecBuilder.DefaultParameters(TestKit.Doc.RootTargets);
        Assert.Equal("{now}", defs.Single(d => d.Key == "SubmittedAt").Default);
        Assert.Equal("Submitter name", defs.Single(d => d.Key == "SubmitterName").Label);
        Assert.All(defs, d => Assert.True(d.Required));
    }

    [Fact]
    public void Resolve_prefers_supplied_then_default_and_reports_all_problems()
    {
        var defs = SpecBuilder.DefaultParameters(TestKit.Doc.RootTargets);
        var ok = RunParameters.Resolve(defs, new Dictionary<string, string> { ["submissionId"] = "A1", ["SubmitterName"] = "Bob" }, Clock);
        Assert.Equal("2026-11-15T10:30:00Z", ok.ByKey["SubmittedAt"]);
        Assert.Equal("A1", ok.ByPath["Submission/@submissionId"]);

        var ex = Assert.Throws<RunException>(() => RunParameters.Resolve(defs,
            new Dictionary<string, string> { ["SubmittedAt"] = "yesterday", ["bogus"] = "1" }, Clock));
        Assert.Equal(4, ex.Problems.Count); // unknown, id required, name required, bad datetime
    }

    [Fact]
    public void Missing_root_value_halts_before_output()
    {
        var spec = TestKit.Build(TestKit.Kind("Products"), ["sku", "product_name", "list_price"]);
        var sb = new StringBuilder();
        using var xw = XmlWriter.Create(sb);
        var ex = Assert.Throws<MappingException>(() => new XmlTransformer().Transform(spec,
            new StringReader("sku,product_name,list_price\nA,B,1\n"), xw, rootValues: new Dictionary<string, string>()));
        Assert.Equal(3, ex.Problems.Count);
        Assert.Equal("", sb.ToString());
    }
}

public class PipelineTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("c2x-test-").FullName;
    public void Dispose() => Directory.Delete(dir, true);

    static readonly Dictionary<string, string> Csvs = new()
    {
        ["Customers"] = "id,customer_name,email,street,city,post_code\n1,Ann,a@x.com,1 Main,Leeds,LS1\n2,Bob,,,,\n",
        ["Orders"] = "order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,2,1.50\n10,1,2024-01-02,B,1,9.99\n11,2,2024-01-03,A,5,1.50\n",
        ["Products"] = "sku,product_name,list_price,discontinued\nA,Widget,1.5,false\nB,Gadget,9.99,true\n",
        ["Invoices"] = "invoice_number,order_id,issued_on,total\nI1,10,2024-02-01,12.99\n",
    };

    (JobSpec job, RunRequest req) Setup(Action<Dictionary<string, string>>? tweak = null, Dictionary<string, string>? parms = null, bool withXsd = true)
    {
        var csvs = new Dictionary<string, string>(Csvs);
        tweak?.Invoke(csvs);
        var inputs = new Dictionary<string, string>();
        var maps = new List<Csv2Xml.Runtime.Mapping.MappingSpec>();
        foreach (var k in TestKit.Doc.Kinds)
        {
            var p = Path.Combine(dir, k.Name + ".csv");
            File.WriteAllText(p, csvs[k.Name]);
            inputs[k.Name] = p;
            maps.Add(TestKit.Build(k, Csvs[k.Name].Split('\n')[0].Split(',')));
        }
        var job = SpecBuilder.BuildJob("Weekly", TestKit.Doc, maps);
        return (job, new RunRequest(inputs, parms ?? new() { ["submissionId"] = "SUB-9", ["SubmitterName"] = "Rob" }, withXsd ? TestKit.XsdText : null));
    }

    string OutDir => Path.Combine(dir, "out");

    const string BadOrders = "order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,many,1.50\n";

    [Fact]
    public async Task Without_xsd_text_output_is_not_validated()
    {
        var (job, req) = Setup(c => c["Orders"] = BadOrders, withXsd: false);
        var res = await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req);
        Assert.True(File.Exists(Path.Combine(OutDir, res.PackageName)));
    }

    [Fact]
    public async Task Invalid_supplied_xsd_is_reported_not_thrown_raw()
    {
        var (job, req) = Setup();
        var ex = await Assert.ThrowsAsync<RunException>(() =>
            new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req with { XsdText = "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:element/></xs:schema>" }));
        Assert.StartsWith("supplied XSD is invalid", ex.Problems.Single());
    }

    [Fact]
    public async Task Mapping_designed_against_another_xsd_is_refused_before_output()
    {
        var (job, req) = Setup();
        var ex = await Assert.ThrowsAsync<RunException>(() =>
            new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req with { XsdText = TestKit.XsdText + "<!-- edited -->" }));
        Assert.Equal(4, ex.Problems.Count(p => p.Contains("designed against a different XSD")));
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Unknown_encoding_and_undecodable_input_are_preflight_problems()
    {
        var (job, req) = Setup();
        File.WriteAllBytes(req.Inputs["Products"], System.Text.Encoding.Latin1.GetBytes("sku,product_name,list_pricé\nA,B,1\n"));
        job = job with { Mappings = job.Mappings.Select(m => m.Kind == "Invoices" ? m with { Encoding = "no-such-encoding" } : m).ToList() };
        var ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req));
        Assert.Contains("Invoices: unknown encoding 'no-such-encoding'", ex.Problems);
        Assert.Contains(ex.Problems, p => p.StartsWith("Products: header row: input contains bytes that are not valid"));
        Assert.False(Directory.Exists(OutDir));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Xml_declaration_is_omitted_unless_the_job_asks_for_it(bool omit)
    {
        var (job, req) = Setup();
        var res = await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job with { OmitXmlDeclaration = omit }, req);
        using var za = ZipFile.OpenRead(res.Receipt);
        var text = new StreamReader(za.GetEntry("Orders.xml")!.Open()).ReadToEnd();
        Assert.Equal(!omit, text.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
        Assert.StartsWith(omit ? "<Submission" : "<?xml", text);
    }

    [Fact]
    public void Stored_job_without_the_declaration_setting_defaults_to_omitting_it()
    {
        var (job, _) = Setup();
        var node = System.Text.Json.Nodes.JsonNode.Parse(Csv2Xml.Runtime.Mapping.ConfigJson.ToJson(job with { OmitXmlDeclaration = false }))!;
        Assert.True(node["content"]!.AsObject().Remove("omitXmlDeclaration"));
        var json = node.ToJsonString();
        Assert.True(Csv2Xml.Runtime.Mapping.ConfigJson.JobFromJson(json).OmitXmlDeclaration);
    }

    [Fact]
    public async Task Four_csvs_become_one_validated_zip_with_report()
    {
        var (job, req) = Setup();
        var res = await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req);

        var zipPath = Assert.Single(Directory.GetFiles(OutDir));
        Assert.Equal(res.Receipt, zipPath);
        Assert.StartsWith("Weekly_", res.PackageName);

        using var za = ZipFile.OpenRead(zipPath);
        Assert.Equal(["Customers.xml", "Invoices.xml", "Orders.xml", "Products.xml", "report.txt"], za.Entries.Select(e => e.FullName).Order());

        var orders = System.Xml.Linq.XDocument.Load(za.GetEntry("Orders.xml")!.Open());
        Assert.Equal("SUB-9", (string?)orders.Root!.Attribute("submissionId"));
        Assert.Equal("Rob", (string?)orders.Root.Element("SubmitterName"));
        Assert.Equal(2, orders.Root.Element("Orders")!.Elements("Order").Count());

        var report = new StreamReader(za.GetEntry("report.txt")!.Open()).ReadToEnd();
        Assert.Contains("[Orders] Orders.csv", report);
        Assert.Contains("data rows: 3", report);
        Assert.Contains("SubmitterName = Rob", report);
        Assert.Equal(4, res.Report.Inputs.Count);
        Assert.All(res.Report.Inputs, i => Assert.True(i.InputBytes > 0 && i.OutputBytes > 0));
    }

    [Fact]
    public async Task One_bad_input_stops_everything_and_sends_nothing()
    {
        var (job, req) = Setup();
        File.WriteAllText(req.Inputs["Invoices"], "invoice_number,issued_on,total\nI1,2024-02-01,1\n"); // no order id column
        var ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req));
        Assert.All(ex.Problems, p => Assert.StartsWith("Invoices:", p)); // shifted layout: every moved column reported
        Assert.Contains(ex.Problems, p => p.Contains("column 2 expected header 'order_id'"));
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Missing_input_or_parameter_is_reported_together()
    {
        var (job, req) = Setup(parms: new());
        var req2 = req with { Inputs = req.Inputs.Where(kv => kv.Key != "Products").ToDictionary() };
        var ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req2));
        Assert.Equal(2, ex.Problems.Count); // submission id + submitter name; products checked next
        ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req with { Parameters = new Dictionary<string, string> { ["submissionId"] = "1", ["SubmitterName"] = "x" }, Inputs = req2.Inputs }));
        Assert.Contains("Products: no input file supplied", ex.Problems);
    }

    [Fact]
    public async Task Output_that_violates_the_xsd_is_not_sent()
    {
        var (job, req) = Setup(c => c["Orders"] = "order_id,customer_id,order_date,sku,quantity,unit_price\n10,1,2024-01-02,A,many,1.50\n");
        var ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req));
        Assert.Contains("Orders output invalid", ex.Problems[0]);
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Skip_policy_records_row_errors_in_report()
    {
        var (job, req) = Setup(c => c["Products"] = "sku,product_name,list_price,discontinued\nA,Widget,1.5,false\nB,,9.99,true\n");
        var res = await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job with { ErrorPolicy = ErrorPolicy.SkipRow }, req);
        var p = res.Report.Inputs.Single(i => i.Kind == "Products");
        Assert.Equal(1, p.RowsWritten);
        Assert.Single(p.RowErrors);
    }

    string TestPluginFolder()
    {
        var plugins = Path.Combine(dir, "test-plugins"); Directory.CreateDirectory(plugins);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Csv2Xml.TestPlugin.dll"), Path.Combine(plugins, "Csv2Xml.TestPlugin.dll"));
        return plugins;
    }

    [Theory]
    [InlineData("throw", "plugin failed: boom")]
    [InlineData("duplicate", "package would contain duplicate entries: CUSTOMERS.XML")]
    public async Task Misbehaving_plugin_stops_the_run_and_nothing_is_sent(string mode, string problem)
    {
        var (job, req) = Setup(parms: new() { ["submissionId"] = "S", ["SubmitterName"] = mode });
        var ex = await Assert.ThrowsAsync<RunException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req, TestPluginFolder()));
        Assert.Equal([problem], ex.Problems.Select(p => p.Replace("Customers.xml", "CUSTOMERS.XML")));
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Cancelled_run_sends_nothing()
    {
        var (job, req) = Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req, ct: cts.Token));
        Assert.False(Directory.Exists(OutDir));
    }

    [Fact]
    public async Task Plugin_can_edit_report_and_add_files()
    {
        var plugins = Path.Combine(dir, "plugins"); Directory.CreateDirectory(plugins);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Csv2Xml.SamplePlugin.dll"), Path.Combine(plugins, "Csv2Xml.SamplePlugin.dll"));
        var (job, req) = Setup();
        var res = await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req, plugins);

        Assert.Contains("SampleMiddleware.Transform", res.Report.Plugin);
        using var za = ZipFile.OpenRead(res.Receipt);
        Assert.NotNull(za.GetEntry("extra/readme.txt"));
        Assert.Contains("Sample plugin: 8 data rows across 4 inputs.", new StreamReader(za.GetEntry("report.txt")!.Open()).ReadToEnd());
    }

    [Fact]
    public async Task Empty_or_missing_plugin_folder_is_ignored()
    {
        var (job, req) = Setup();
        Directory.CreateDirectory(Path.Combine(dir, "none"));
        var res = await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req, Path.Combine(dir, "none"));
        Assert.Null(res.Report.Plugin);
        await new RunPipeline(new FileOutputSink(OutDir)).RunAsync(job, req, Path.Combine(dir, "absent"));
    }

    [Fact]
    public async Task Http_sink_posts_the_zip_as_one_body()
    {
        var handler = new CaptureHandler(HttpStatusCode.Accepted);
        var sink = new HttpPostOutputSink(new HttpClient(handler), new Uri("https://example.test/in"), r => r.Headers.Add("X-Key", "k"));
        var (job, req) = Setup();
        var res = await new RunPipeline(sink).RunAsync(job, req);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("application/zip", handler.ContentType);
        Assert.Equal("k", handler.Key);
        Assert.Equal(res.PackageName, handler.FileName);
        using var za = new ZipArchive(new MemoryStream(handler.Body!));
        Assert.Equal(5, za.Entries.Count);
        Assert.Equal("202 https://example.test/in", res.Receipt);
    }

    [Fact]
    public async Task Http_failure_surfaces_with_body()
    {
        var sink = new HttpPostOutputSink(new HttpClient(new CaptureHandler(HttpStatusCode.Unauthorized)), new Uri("https://example.test/in"));
        var (job, req) = Setup();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new RunPipeline(sink).RunAsync(job, req));
        Assert.Contains("401", ex.Message);
    }

    sealed class CaptureHandler(HttpStatusCode code) : HttpMessageHandler
    {
        public int Calls; public byte[]? Body; public string? ContentType, FileName, Key;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++;
            Body = await r.Content!.ReadAsByteArrayAsync(ct);
            ContentType = r.Content.Headers.ContentType?.MediaType;
            FileName = r.Content.Headers.ContentDisposition?.FileName;
            Key = r.Headers.TryGetValues("X-Key", out var v) ? v.Single() : null;
            return new HttpResponseMessage(code) { Content = new StringContent(code == HttpStatusCode.Accepted ? "ok" : "denied") };
        }
    }
}
