using System.Net.Http.Headers;
using System.Text;
using Habita.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Habita.Infrastructure.Services;

/// <summary>
/// PDF through Gotenberg (LibreOffice for Word files, Chromium for HTML), a service that runs on the internal network only and is never reachable from outside.
/// Word files are filled by this application first; Gotenberg only converts what it is handed.
/// </summary>
public class GotenbergPdfConverter(IConfiguration config) : IPdfConverter
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

    public Task<byte[]> FromDocxAsync(byte[] docx, CancellationToken ct) =>
        PostAsync("/forms/libreoffice/convert", "contrato.docx", docx, [], ct);

    public Task<byte[]> FromHtmlAsync(string html, CancellationToken ct) =>
        PostAsync("/forms/chromium/convert/html", "index.html", Encoding.UTF8.GetBytes(html),
            [("preferCssPageSize", "true"), ("printBackground", "true"), ("emulatedMediaType", "print")], ct);

    private async Task<byte[]> PostAsync(string path, string fileName, byte[] content, (string Name, string Value)[] fields, CancellationToken ct)
    {
        var baseUrl = config["Pdf:GotenbergUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new PdfUnavailableException("La generación de PDF no está configurada en este servidor.");
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "files", fileName);
            foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
            using var response = await Http.PostAsync(baseUrl.TrimEnd('/') + path, form, ct);
            if (!response.IsSuccessStatusCode) throw new PdfUnavailableException("No se pudo generar el PDF. Inténtalo de nuevo en un momento.");
            var pdf = await response.Content.ReadAsByteArrayAsync(ct);
            if (pdf.Length < 5 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F') throw new PdfUnavailableException("El servicio de PDF no devolvió un PDF válido.");
            return pdf;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new PdfUnavailableException("No se pudo generar el PDF. Inténtalo de nuevo en un momento.", e);
        }
    }
}
