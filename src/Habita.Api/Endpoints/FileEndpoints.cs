using Habita.Api.Auth;
using Habita.Application.Files;
using Habita.Application.Members;

namespace Habita.Api.Endpoints;

public static class FileEndpoints
{
    /// <summary>
    /// Private files. Any member can upload (a tenant uploads a payment proof); who can open a file is decided by the service:
    /// the uploader, or a reviewing staff role. Files are never served from a public folder.
    /// </summary>
    public static void MapFileEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/files").RequireAuthorization(Policies.AnyMember);
        // Antiforgery is off because CSRF is handled by the SameSite cookie plus OriginCheckMiddleware.
        g.MapPost("", Upload).DisableAntiforgery().RequireRateLimiting(Habita.Api.Security.RateLimiting.Upload);
        g.MapGet("/{id:guid}", Download);
    }

    static async Task<IResult> Upload(HttpRequest request, FileService svc, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.Problem("Envía el archivo como multipart/form-data.", statusCode: 400);
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count != 1) return Results.Problem("Envía exactamente un archivo.", statusCode: 400);
        var file = form.Files[0];
        await using var stream = file.OpenReadStream();
        return (await svc.SaveAsync(stream, file.FileName, ct)).ToResult(f => Results.Created($"/api/v1/files/{f.Id}", f));
    }

    static async Task<IResult> Download(Guid id, FileService svc, CancellationToken ct)
    {
        var r = await svc.OpenAsync(id, ct);
        if (r.Outcome != Outcome.Ok) return r.ToResult();
        // attachment + nosniff: the browser saves it instead of rendering it inside our origin.
        return Results.File(r.Value!.Content, r.Value.File.ContentType, r.Value.File.Name, enableRangeProcessing: false);
    }
}
