using System.Net.Http.Headers;

namespace Csv2Xml.Runtime.Output;

/// <summary>The finished zip; opened lazily so a sink can stream it.</summary>
public sealed record OutputPackage(string FileName, Func<Stream> Open, long Length);

/// <summary>Delivers a finished package. Implementations own transport details (auth, certificates, retries).</summary>
public interface IOutputSink
{
    /// <returns>A receipt: where it went / what the endpoint said.</returns>
    Task<string> SendAsync(OutputPackage package, CancellationToken ct = default);
}

/// <summary>Default sink: writes into a folder, atomically (temp name then move).</summary>
public sealed class FileOutputSink(string folder) : IOutputSink
{
    public async Task<string> SendAsync(OutputPackage package, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var final = Path.Combine(folder, package.FileName);
        var tmp = final + ".partial";
        try
        {
            await using (var src = package.Open())
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            File.Move(tmp, final, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } // never leave a partial file behind
            throw;
        }
        return final;
    }
}

/// <summary>Default sink: POSTs the zip as the request body. Auth/certs come from the supplied HttpClient and/or configure hook.</summary>
public sealed class HttpPostOutputSink(HttpClient client, Uri endpoint, Action<HttpRequestMessage>? configure = null) : IOutputSink
{
    public async Task<string> SendAsync(OutputPackage package, CancellationToken ct = default)
    {
        await using var body = package.Open();
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StreamContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        req.Content.Headers.ContentLength = package.Length;
        req.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = package.FileName };
        configure?.Invoke(req);

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} from {endpoint}: {text[..Math.Min(text.Length, 300)]}");
        }
        return $"{(int)resp.StatusCode} {endpoint}";
    }
}
