using Habita.Api.Auth;
using Habita.Application.Imports;
using Habita.Application.Members;

namespace Habita.Api.Endpoints;

public static class ImportEndpoints
{
    public const long MaxFileBytes = 5_000_000;

    /// <summary>
    /// Import of the landlord's spreadsheet (Hito 15), Owner and Admin only. Both steps receive the file: the preview changes nothing; the confirmation reads the same file again
    /// and refuses to save if it (or what is already in Habita) no longer matches what was previewed. The file is never stored.
    /// </summary>
    public static void MapImportEndpoints(this RouteGroupBuilder v1)
    {
        var imports = v1.MapGroup("/imports").RequireAuthorization(Policies.ManageProperties);
        imports.MapGet("/template.csv", () => Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(ImportService.TemplateCsv)).ToArray(), "text/csv; charset=utf-8", "Plantilla de inquilinos.csv"));

        imports.MapPost("/preview", async (HttpRequest request, ImportService svc, CancellationToken ct) =>
        {
            var read = await ReadAsync(request, ct);
            return read.Error is not null ? Results.Problem(read.Error, statusCode: 400) : (await svc.PreviewAsync(read.File!, read.Options!, ct)).ToResult();
        }).DisableAntiforgery().RequireRateLimiting(Habita.Api.Security.RateLimiting.Upload);

        imports.MapPost("/confirm", async (HttpRequest request, ImportService svc, CancellationToken ct) =>
        {
            var read = await ReadAsync(request, ct);
            return read.Error is not null ? Results.Problem(read.Error, statusCode: 400)
                : (await svc.ConfirmAsync(read.File!, read.Options!, read.PlanHash, read.SkipErrors, ct)).ToResult();
        }).DisableAntiforgery().RequireRateLimiting(Habita.Api.Security.RateLimiting.Upload);
    }

    private sealed record Read(byte[]? File, ImportOptions? Options, string? PlanHash, bool SkipErrors, string? Error);

    private static async Task<Read> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return new(null, null, null, false, "Envía el archivo como multipart/form-data.");
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count != 1) return new(null, null, null, false, "Envía exactamente un archivo.");
        var file = form.Files[0];
        if (file.Length is 0 or > MaxFileBytes) return new(null, null, null, false, "El archivo debe pesar entre 1 byte y 5 MB.");
        if (!Guid.TryParse(form["propertyId"], out var propertyId)) return new(null, null, null, false, "Indica la propiedad a la que se importa.");
        await using var stream = file.OpenReadStream(); using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        bool Flag(string key) => string.Equals(form[key].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        return new(ms.ToArray(), new ImportOptions(propertyId, Flag("createMissingUnits"), Flag("activateLeases")), form["planHash"].ToString(), Flag("skipErrors"), null);
    }
}
