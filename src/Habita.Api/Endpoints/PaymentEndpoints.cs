using Habita.Api.Auth;
using Habita.Application.Members;
using Habita.Application.Payments;
using Habita.Domain.Payments;
using Microsoft.AspNetCore.Mvc;

namespace Habita.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/payments").RequireAuthorization(Policies.ReadFinance);
        var write = v1.MapGroup("/payments").RequireAuthorization(Policies.WritePayments);
        var admin = v1.MapGroup("/payments").RequireAuthorization(Policies.ManageProperties); // Owner/Admin: reversals

        read.MapGet("", async (PaymentService svc, Guid? tenantId, PaymentStatus? status, DateOnly? from, DateOnly? to, string? search, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(tenantId, status, from, to, search, page ?? 1, pageSize ?? 25, ct)));
        read.MapGet("/{id:guid}", async (Guid id, PaymentService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

        // Idempotency-Key: a retry of the same request returns the original payment (200) instead of creating another (201).
        write.MapPost("", async (CreatePaymentRequest req, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpResponse response, PaymentService svc, CancellationToken ct) =>
        {
            var r = await svc.CreateAsync(req, idempotencyKey, ct);
            if (r.Outcome != Outcome.Ok) return r.ToResult();
            if (r.Value!.Replayed) { response.Headers["Idempotent-Replayed"] = "true"; return Results.Ok(r.Value.Payment); }
            return Results.Created($"/api/v1/payments/{r.Value.Payment.Id}", r.Value.Payment);
        });
        write.MapPost("/{id:guid}/allocations", async (Guid id, AllocatePaymentRequest req, PaymentService svc, CancellationToken ct) =>
            (await svc.AllocateAsync(id, req, ct)).ToResult());

        admin.MapPost("/{id:guid}/reverse", async (Guid id, ReasonRequest req, PaymentService svc, CancellationToken ct) => (await svc.ReverseAsync(id, req, ct)).ToResult());
        admin.MapPost("/{id:guid}/allocations/{allocationId:guid}/reverse", async (Guid id, Guid allocationId, ReasonRequest req, PaymentService svc, CancellationToken ct) =>
            (await svc.ReverseAllocationAsync(id, allocationId, req, ct)).ToResult());
    }
}
