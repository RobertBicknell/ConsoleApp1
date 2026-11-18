using System.IO.Compression;
using System.Text;
using System.Xml;
using Csv2Xml.Runtime.Csv;
using Csv2Xml.Runtime.Output;
using Csv2Xml.Runtime.Validation;
using Csv2Xml.Runtime.Writing;

namespace Csv2Xml.Runtime.Run;

public sealed record RunResult(RunReport Report, string PackageName, long PackageBytes, string Receipt);

/// <summary>
/// One run: resolve run values, preflight every input (nothing is written if any fails), transform each CSV to XML in a temp
/// folder, validate against the XSD (when the request supplies it), build the report, run the plugin, zip, send. Nothing is sent unless every step succeeded.
/// </summary>
public sealed class RunPipeline(IOutputSink sink, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<RunResult> RunAsync(JobSpec job, RunRequest request, string? pluginFolder = null, CancellationToken ct = default)
    {
        var started = clock.GetUtcNow();
        var values = RunParameters.Resolve(job.Parameters, request.Parameters, clock);
        var schemas = CompileRequestXsd(request);
        Preflight(job, request, values, request.XsdText is null ? null : XsdValidator.Hash(request.XsdText));

        var tmp = Directory.CreateTempSubdirectory("csv2xml-");
        try
        {
            var report = new RunReport { JobName = job.Name, Started = started };
            foreach (var (k, v) in values.ByKey) report.Parameters[k] = v;
            var files = new List<PackageFile>();

            foreach (var m in job.Mappings)
            {
                ct.ThrowIfCancellationRequested();
                var inPath = request.Inputs[m.Kind];
                var xmlName = m.Kind + ".xml";
                var xmlPath = Path.Combine(tmp.FullName, xmlName);

                TransformResult res;
                using (var csv = CsvParser.Open(inPath, m.Encoding))
                using (var fs = File.Create(xmlPath))
                using (var xw = XmlWriter.Create(fs, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = job.OmitXmlDeclaration }))
                    res = new XmlTransformer().Transform(m, csv, xw, job.ErrorPolicy, ct, values.ByPath);

                if (res.Halted)
                    throw new RunException(res.Errors.Select(e => $"{m.Kind} row {e.Row}: {e.Message}").ToList());

                if (job.ValidateOutput && schemas is not null)
                {
                    var v = XsdValidator.ValidateFile(xmlPath, schemas, 10);
                    if (!v.IsValid)
                        throw new RunException(v.Issues.Select(i => $"{m.Kind} output invalid at {i.Line}:{i.Column}: {i.Message}").ToList());
                }

                report.Inputs.Add(new InputReport(m.Kind, Path.GetFileName(inPath), new FileInfo(inPath).Length,
                    res.RowsRead - (m.HasHeader ? 1 : 0) - res.BlankRows, res.RowsWritten,
                    res.Errors.Select(e => $"row {e.Row}: {e.Message}").ToList(), xmlName, new FileInfo(xmlPath).Length, res.BlankRows));
                files.Add(PackageFile.FromFile(xmlName, xmlPath));
            }

            var plugin = PluginRunner.Find(pluginFolder);
            report.Plugin = plugin?.Name;
            report.Finished = clock.GetUtcNow();

            var ctx = new Context
            {
                Job = job, Parameters = values.ByKey, Report = report, Files = files,
                ReportText = report.Render(),
            };
            plugin?.Invoke(ctx);

            var zipName = job.PackageName.Replace("{Name}", job.Name).Replace("{timestamp}", started.ToString("yyyyMMdd'T'HHmmss'Z'"));
            if (!zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipName += ".zip";
            var zipPath = Path.Combine(tmp.FullName, "package.zip");

            using (var za = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entries = ctx.Files.Where(f => !string.Equals(f.Name, "report.txt", StringComparison.OrdinalIgnoreCase))
                    .Append(PackageFile.FromText("report.txt", ctx.ReportText)).ToList();
                var dup = entries.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dup.Count > 0) throw new RunException([$"package would contain duplicate entries: {string.Join(", ", dup)}"]);
                foreach (var f in entries)
                {
                    using var src = f.Open();
                    using var dst = za.CreateEntry(f.Name, CompressionLevel.Optimal).Open();
                    src.CopyTo(dst);
                }
            }

            var length = new FileInfo(zipPath).Length;
            var receipt = await sink.SendAsync(new OutputPackage(zipName, () => File.OpenRead(zipPath), length), ct);
            return new RunResult(report, zipName, length, receipt);
        }
        finally { try { tmp.Delete(recursive: true); } catch (IOException) { } }
    }

    static System.Xml.Schema.XmlSchemaSet? CompileRequestXsd(RunRequest request)
    {
        if (request.XsdText is null) return null;
        try { return XsdValidator.Compile(request.XsdText); }
        catch (Exception e) when (e is System.Xml.Schema.XmlSchemaException or XmlException)
        { throw new RunException([$"supplied XSD is invalid: {e.Message}"]); }
    }

    static void Preflight(JobSpec job, RunRequest request, ResolvedParameters values, string? xsdHash)
    {
        var problems = new List<string>();
        problems.AddRange(job.Mappings.GroupBy(m => m.Kind).Where(g => g.Count() > 1).Select(g => $"job has {g.Count()} mappings for kind '{g.Key}'"));
        problems.AddRange(request.Inputs.Keys.Where(k => job.Mappings.All(m => m.Kind != k)).Select(k => $"input supplied for unknown kind '{k}'"));

        foreach (var m in job.Mappings)
        {
            if (xsdHash is not null && m.XsdHash is not null && !string.Equals(xsdHash, m.XsdHash, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{m.Kind}: mapping was designed against a different XSD; rebuild the mapping");
            if (!request.Inputs.TryGetValue(m.Kind, out var path)) { problems.Add($"{m.Kind}: no input file supplied"); continue; }
            if (!File.Exists(path)) { problems.Add($"{m.Kind}: input not found: {path}"); continue; }
            try
            {
                string[]? header = null;
                if (m.HasHeader)
                {
                    using var r = CsvParser.Open(path, m.Encoding);
                    header = CsvParser.Read(r, m.Delimiter).FirstOrDefault();
                }
                ColumnBinder.Bind(m, header);
            }
            catch (MappingException e) { problems.AddRange(e.Problems.Select(p => $"{m.Kind}: {p}")); }
            catch (Exception e) when (CsvParser.DescribeInputError(e) is { } msg) { problems.Add($"{m.Kind}: header row: {msg}"); }
            catch (ArgumentException) when (m.Encoding is not null) { problems.Add($"{m.Kind}: unknown encoding '{m.Encoding}'"); }

            problems.AddRange((m.RootFields ?? []).Where(f => f.Required && !values.ByPath.ContainsKey(f.Path))
                .Select(f => $"{m.Kind}: required run value '{f.Path}' has no parameter"));
        }
        if (problems.Count > 0) throw new RunException(problems);
    }
}
