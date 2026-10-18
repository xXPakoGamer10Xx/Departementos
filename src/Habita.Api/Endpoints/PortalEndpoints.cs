using Habita.Api.Auth;
using Habita.Application.Collections;
using Habita.Application.Portal;

namespace Habita.Api.Endpoints;

public static class PortalEndpoints
{
    /// <summary>Only for a Tenant login. Nothing here takes a tenant id: the tenant is the signed-in membership.</summary>
    public static void MapPortalEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/portal").RequireAuthorization(Policies.TenantOnly);
        g.MapGet("/summary", async (PortalService svc, CancellationToken ct) => (await svc.SummaryAsync(ct)).ToResult());
        g.MapGet("/charges", async (PortalService svc, bool? pendingOnly, int? page, int? pageSize, CancellationToken ct) =>
            (await svc.ChargesAsync(pendingOnly ?? false, page ?? 1, pageSize ?? 20, ct)).ToResult());
        g.MapGet("/payments", async (PortalService svc, int? page, int? pageSize, CancellationToken ct) =>
            (await svc.PaymentsAsync(page ?? 1, pageSize ?? 20, ct)).ToResult());
        g.MapGet("/issues", async (PortalService svc, int? page, int? pageSize, CancellationToken ct) =>
            (await svc.IssuesAsync(page ?? 1, pageSize ?? 20, ct)).ToResult());
        g.MapGet("/issues/{id:guid}", async (Guid id, PortalService svc, CancellationToken ct) => (await svc.GetIssueAsync(id, ct)).ToResult());
        g.MapPost("/issues", async (ReportIssueRequest req, PortalService svc, CancellationToken ct) =>
            (await svc.ReportIssueAsync(req, ct)).ToResult(i => Results.Created($"/api/v1/portal/issues/{i.Id}", i)));
        g.MapPost("/issues/{id:guid}/comments", async (Guid id, PortalCommentRequest req, PortalService svc, CancellationToken ct) =>
            (await svc.CommentAsync(id, req, ct)).ToResult());

        g.MapPortalDocumentEndpoints(b => b.RequireRateLimiting(Habita.Api.Security.RateLimiting.Pdf));

        // Paying: where to pay, report a payment with its proof, and a cash QR for whoever collects.
        g.MapGet("/payment-info", async (PortalCollectionService svc, CancellationToken ct) => (await svc.PaymentInfoAsync(ct)).ToResult());
        g.MapGet("/payment-claims", async (PortalCollectionService svc, int? page, int? pageSize, CancellationToken ct) =>
            (await svc.ClaimsAsync(page ?? 1, pageSize ?? 10, ct)).ToResult());
        g.MapPost("/payment-claims", async (ReportPaymentRequest req, PortalCollectionService svc, CancellationToken ct) =>
            (await svc.ReportAsync(req, ct)).ToResult(c => Results.Created($"/api/v1/portal/payment-claims/{c.Id}", c)));
        g.MapPost("/payment-claims/{id:guid}/cancel", async (Guid id, PortalCollectionService svc, CancellationToken ct) => (await svc.CancelClaimAsync(id, ct)).ToResult());
        g.MapPost("/cash-token", async (PortalCollectionService svc, CancellationToken ct) =>
            (await svc.CreateCashTokenAsync(ct)).ToResult(t => Results.Created($"/api/v1/portal/cash-token/{t.Id}", t)));
        g.MapDelete("/cash-token", async (PortalCollectionService svc, CancellationToken ct) => (await svc.RevokeCashTokenAsync(ct)).ToResult(_ => Results.NoContent()));
    }
}
