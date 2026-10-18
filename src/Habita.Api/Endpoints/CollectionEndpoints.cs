using Habita.Api.Auth;
using Habita.Application.Collections;
using Habita.Application.Members;
using Habita.Domain.Collections;

namespace Habita.Api.Endpoints;

public static class CollectionEndpoints
{
    /// <summary>
    /// Collections (Hito 12). Bank accounts are managed by Owner/Admin and read by whoever reads reports. Payment reports, promises and the cash QR
    /// belong to the people who record payments (Owner, Admin, Finance, Collector); a Tenant only reaches them through /portal.
    /// </summary>
    public static void MapCollectionEndpoints(this RouteGroupBuilder v1)
    {
        var accountsRead = v1.MapGroup("/bank-accounts").RequireAuthorization(Policies.ReadReports);
        var accountsWrite = v1.MapGroup("/bank-accounts").RequireAuthorization(Policies.ManageProperties);
        accountsRead.MapGet("", async (BankAccountService svc, bool? includeArchived, CancellationToken ct) => Results.Ok(await svc.ListAsync(includeArchived ?? false, ct)));
        accountsWrite.MapPost("", async (CreateBankAccountRequest req, BankAccountService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(a => Results.Created($"/api/v1/bank-accounts/{a.Id}", a)));
        accountsWrite.MapPatch("/{id:guid}", async (Guid id, UpdateBankAccountRequest req, BankAccountService svc, CancellationToken ct) => (await svc.UpdateAsync(id, req, ct)).ToResult());
        accountsWrite.MapPost("/{id:guid}/archive", async (Guid id, BankAccountService svc, CancellationToken ct) => (await svc.ArchiveAsync(id, ct)).ToResult());

        var claims = v1.MapGroup("/payment-claims").RequireAuthorization(Policies.WritePayments);
        claims.MapGet("", async (PaymentClaimService svc, ClaimStatus? status, Guid? tenantId, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(status, tenantId, page ?? 1, pageSize ?? 25, ct)));
        claims.MapGet("/{id:guid}", async (Guid id, PaymentClaimService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());
        claims.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmClaimRequest req, PaymentClaimService svc, CancellationToken ct) => (await svc.ConfirmAsync(id, req, ct)).ToResult());
        claims.MapPost("/{id:guid}/reject", async (Guid id, RejectClaimRequest req, PaymentClaimService svc, CancellationToken ct) => (await svc.RejectAsync(id, req, ct)).ToResult());

        var promisesRead = v1.MapGroup("/payment-promises").RequireAuthorization(Policies.ReadFinance);
        var promisesWrite = v1.MapGroup("/payment-promises").RequireAuthorization(Policies.WritePayments);
        promisesRead.MapGet("", async (PaymentPromiseService svc, Guid? tenantId, Guid? chargeId, PromiseStatus? status, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(tenantId, chargeId, status, page ?? 1, pageSize ?? 25, ct)));
        promisesWrite.MapPost("", async (CreatePromiseRequest req, PaymentPromiseService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(p => Results.Created($"/api/v1/payment-promises/{p.Id}", p)));
        promisesWrite.MapPost("/{id:guid}/cancel", async (Guid id, CancelPromiseRequest req, PaymentPromiseService svc, CancellationToken ct) => (await svc.CancelAsync(id, req, ct)).ToResult());

        var cash = v1.MapGroup("/cash-tokens").RequireAuthorization(Policies.WritePayments);
        cash.MapPost("/resolve", async (ResolveCashTokenRequest req, CashCollectionService svc, CancellationToken ct) => (await svc.ResolveAsync(req, ct)).ToResult())
            .RequireRateLimiting(Habita.Api.Security.RateLimiting.Scan);
        // 201 the first time; 200 with Idempotent-Replayed when the same collection is sent again (a retry after a dropped connection).
        cash.MapPost("/{id:guid}/collect", async (Guid id, CollectCashRequest req, HttpResponse response, CashCollectionService svc, CancellationToken ct) =>
        {
            var r = await svc.CollectAsync(id, req, ct);
            if (r.Outcome != Outcome.Ok) return r.ToResult();
            if (r.Value!.Replayed) { response.Headers["Idempotent-Replayed"] = "true"; return Results.Ok(r.Value.Payment); }
            return Results.Created($"/api/v1/payments/{r.Value.Payment.Id}", r.Value.Payment);
        });
    }
}
