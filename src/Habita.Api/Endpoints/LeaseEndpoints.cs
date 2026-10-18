using Habita.Api.Auth;
using Habita.Application.Leases;
using Habita.Domain.Leases;

namespace Habita.Api.Endpoints;

public static class LeaseEndpoints
{
    public static void MapLeaseEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/leases").RequireAuthorization(Policies.ReadLeases);
        var write = v1.MapGroup("/leases").RequireAuthorization(Policies.ManageProperties); // Owner/Admin

        read.MapGet("", async (LeaseService svc, LeaseStatus? status, Guid? unitId, Guid? tenantId, string? search, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(status, unitId, tenantId, search, page ?? 1, pageSize ?? 25, ct)));
        read.MapGet("/{id:guid}", async (Guid id, LeaseService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

        write.MapPost("", async (CreateLeaseRequest req, LeaseService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(l => Results.Created($"/api/v1/leases/{l.Id}", l)));
        write.MapPatch("/{id:guid}", async (Guid id, UpdateLeaseRequest req, LeaseService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(id, req, ct)).ToResult());
        write.MapPost("/{id:guid}/activate", async (Guid id, LeaseService svc, CancellationToken ct) => (await svc.ActivateAsync(id, ct)).ToResult());
        write.MapPost("/{id:guid}/terminate", async (Guid id, TerminateLeaseRequest req, LeaseService svc, CancellationToken ct) =>
            (await svc.TerminateAsync(id, req, ct)).ToResult());
        write.MapPost("/{id:guid}/cancel", async (Guid id, CancelLeaseRequest req, LeaseService svc, CancellationToken ct) =>
            (await svc.CancelAsync(id, req, ct)).ToResult());
    }
}
