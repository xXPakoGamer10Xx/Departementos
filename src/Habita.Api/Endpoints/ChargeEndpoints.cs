using Habita.Api.Auth;
using Habita.Application.Charges;
using Habita.Domain.Charges;

namespace Habita.Api.Endpoints;

public static class ChargeEndpoints
{
    public static void MapChargeEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/rent-charges").RequireAuthorization(Policies.ReadFinance);
        var write = v1.MapGroup("/rent-charges").RequireAuthorization(Policies.WriteFinance);
        var admin = v1.MapGroup("/rent-charges").RequireAuthorization(Policies.ManageProperties); // Owner/Admin

        read.MapGet("", async (ChargeService svc, ChargeStatus? status, Guid? leaseId, Guid? tenantId, int? year, int? month, string? search, bool? openOnly, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(status, leaseId, tenantId, year, month, search, openOnly ?? false, page ?? 1, pageSize ?? 25, ct)));
        read.MapGet("/{id:guid}", async (Guid id, ChargeService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

        write.MapPost("", async (CreateChargeRequest req, ChargeService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(c => Results.Created($"/api/v1/rent-charges/{c.Id}", c)));
        write.MapPost("/generate-preview", async (GenerateChargesRequest req, ChargeService svc, CancellationToken ct) => (await svc.PreviewAsync(req, ct)).ToResult());
        write.MapPost("/generate", async (GenerateChargesRequest req, ChargeService svc, CancellationToken ct) => (await svc.GenerateAsync(req, ct)).ToResult());

        admin.MapPost("/{id:guid}/void", async (Guid id, VoidChargeRequest req, ChargeService svc, CancellationToken ct) => (await svc.VoidAsync(id, req, ct)).ToResult());
    }
}
