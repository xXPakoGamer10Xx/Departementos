using Habita.Api.Auth;
using Habita.Application.Documents;
using Habita.Application.Members;

namespace Habita.Api.Endpoints;

public static class DocumentEndpoints
{
    /// <summary>
    /// Documents (Hito 14). The contract template is managed by Owner and Admin and read by whoever reads leases. Generated contracts belong to their lease
    /// (same readers; only Owner and Admin generate). A payment's receipt is read with the payment; a tenant's statement with the reports. Tenants get their
    /// own through /portal. Every PDF is made by an external service, so those routes are rate-limited per person.
    /// </summary>
    public static void MapDocumentEndpoints(this RouteGroupBuilder v1)
    {
        var tplRead = v1.MapGroup("/contract-template").RequireAuthorization(Policies.ReadLeases);
        var tplWrite = v1.MapGroup("/contract-template").RequireAuthorization(Policies.ManageProperties);
        tplRead.MapGet("", async (ContractService svc, CancellationToken ct) => (await svc.ActiveAsync(ct)).ToResult());
        tplRead.MapGet("/fields", () => Results.Ok(ContractService.Fields()));
        tplRead.MapGet("/history", async (ContractService svc, CancellationToken ct) => Results.Ok(await svc.HistoryAsync(ct)));
        tplRead.MapGet("/sample", (ContractService svc) => Results.File(svc.Sample(), Habita.Domain.Documents.ContractTemplate.DocxContentType, "Plantilla de ejemplo.docx"));
        tplRead.MapGet("/{id:guid}/file", async (Guid id, ContractService svc, CancellationToken ct) => (await svc.TemplateFileAsync(id, ct)).AsFile());
        // Antiforgery is off because CSRF is handled by the SameSite cookie plus OriginCheckMiddleware.
        tplWrite.MapPost("", async (HttpRequest request, ContractService svc, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.Problem("Envía el archivo como multipart/form-data.", statusCode: 400);
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1) return Results.Problem("Envía exactamente un archivo.", statusCode: 400);
            await using var stream = form.Files[0].OpenReadStream();
            return (await svc.UploadTemplateAsync(stream, form.Files[0].FileName, ct)).ToResult(t => Results.Created($"/api/v1/contract-template/{t.Id}", t));
        }).DisableAntiforgery().RequireRateLimiting(Habita.Api.Security.RateLimiting.Upload);

        var docsRead = v1.MapGroup("/leases/{leaseId:guid}/documents").RequireAuthorization(Policies.ReadLeases);
        var docsWrite = v1.MapGroup("/leases/{leaseId:guid}/documents").RequireAuthorization(Policies.ManageProperties);
        docsRead.MapGet("", async (Guid leaseId, ContractService svc, CancellationToken ct) => (await svc.ListAsync(leaseId, ct)).ToResult());
        docsRead.MapGet("/{documentId:guid}/pdf", async (Guid leaseId, Guid documentId, ContractService svc, CancellationToken ct) =>
            (await svc.DownloadAsync(leaseId, documentId, DocumentKind.Pdf, ct)).AsFile()).RequireRateLimiting(Habita.Api.Security.RateLimiting.Pdf);
        docsRead.MapGet("/{documentId:guid}/docx", async (Guid leaseId, Guid documentId, ContractService svc, CancellationToken ct) =>
            (await svc.DownloadAsync(leaseId, documentId, DocumentKind.Docx, ct)).AsFile()).RequireRateLimiting(Habita.Api.Security.RateLimiting.Pdf);
        docsWrite.MapPost("", async (Guid leaseId, ContractService svc, CancellationToken ct) =>
            (await svc.GenerateAsync(leaseId, ct)).ToResult(d => Results.Created($"/api/v1/leases/{leaseId}/documents/{d.Id}", d))).RequireRateLimiting(Habita.Api.Security.RateLimiting.Pdf);

        var receipts = v1.MapGroup("/payments/{id:guid}").RequireAuthorization(Policies.ReadFinance);
        receipts.MapGet("/receipt", async (Guid id, ReceiptService svc, CancellationToken ct) => (await svc.InfoAsync(id, ct)).ToResult());
        receipts.MapGet("/receipt.pdf", async (Guid id, ReceiptService svc, CancellationToken ct) => (await svc.PdfAsync(id, ct)).AsFile()).RequireRateLimiting(Habita.Api.Security.RateLimiting.Pdf);

        v1.MapGet("/tenants/{id:guid}/statement.pdf", async (Guid id, StatementService svc, CancellationToken ct) => (await svc.PdfAsync(id, ct)).AsFile())
            .RequireAuthorization(Policies.ReadReports).RequireRateLimiting(Habita.Api.Security.RateLimiting.Pdf);
    }

    /// <summary>The documents a tenant login may download. Nothing here takes a tenant id: it is always the signed-in tenant's own.</summary>
    public static void MapPortalDocumentEndpoints(this RouteGroupBuilder portal, Action<RouteHandlerBuilder> limit)
    {
        limit(portal.MapGet("/payments/{id:guid}/receipt.pdf", async (Guid id, ReceiptService svc, CancellationToken ct) => (await svc.PdfForTenantAsync(id, ct)).AsFile()));
        limit(portal.MapGet("/statement.pdf", async (StatementService svc, CancellationToken ct) => (await svc.PdfForTenantAsync(ct)).AsFile()));
        limit(portal.MapGet("/contract.pdf", async (ContractService svc, CancellationToken ct) => (await svc.LatestPdfForTenantAsync(ct)).AsFile()));
    }

    // Generated documents are always offered as downloads (attachment): the browser saves them instead of rendering them inside our origin.
    private static IResult AsFile(this Result<PdfFile> r) => r.Outcome == Outcome.Ok ? Results.File(r.Value!.Bytes, "application/pdf", r.Value.FileName) : r.ToResult();
    private static IResult AsFile(this Result<DownloadFile> r) => r.Outcome == Outcome.Ok ? Results.File(r.Value!.Bytes, r.Value.ContentType, r.Value.FileName) : r.ToResult();
}
