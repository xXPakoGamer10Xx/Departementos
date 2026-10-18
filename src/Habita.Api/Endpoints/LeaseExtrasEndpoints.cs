using Habita.Api.Auth;
using Habita.Application.Deposits;
using Habita.Application.Leases;

namespace Habita.Api.Endpoints;

public static class LeaseExtrasEndpoints
{
    /// <summary>
    /// What hangs from a lease besides its terms: the inventory handed over and the deposit. Reading follows the lease (Owner, Admin, Finance, ReadOnly);
    /// changing it is for Owner and Admin, like the lease itself.
    /// </summary>
    public static void MapLeaseExtrasEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/leases/{leaseId:guid}").RequireAuthorization(Policies.ReadLeases);
        var write = v1.MapGroup("/leases/{leaseId:guid}").RequireAuthorization(Policies.ManageProperties);

        read.MapGet("/inventory", async (Guid leaseId, InventoryService svc, CancellationToken ct) => (await svc.ListAsync(leaseId, ct)).ToResult());
        write.MapPut("/inventory", async (Guid leaseId, ReplaceInventoryRequest req, InventoryService svc, CancellationToken ct) => (await svc.ReplaceAsync(leaseId, req, ct)).ToResult());
        write.MapPost("/inventory/return", async (Guid leaseId, RecordReturnRequest req, InventoryService svc, CancellationToken ct) => (await svc.RecordReturnAsync(leaseId, req, ct)).ToResult());

        read.MapGet("/deposit", async (Guid leaseId, DepositService svc, CancellationToken ct) => (await svc.GetAsync(leaseId, ct)).ToResult());
        write.MapPost("/deposit/schedule", async (Guid leaseId, ScheduleDepositRequest req, DepositService svc, CancellationToken ct) =>
            (await svc.ScheduleAsync(leaseId, req, ct)).ToResult(d => Results.Created($"/api/v1/leases/{leaseId}/deposit", d)));
        write.MapPost("/deposit/settle", async (Guid leaseId, SettleDepositRequest req, DepositService svc, CancellationToken ct) =>
            (await svc.SettleAsync(leaseId, req, ct)).ToResult(d => Results.Created($"/api/v1/leases/{leaseId}/deposit", d)));
        write.MapPost("/deposit/settlement/void", async (Guid leaseId, VoidSettlementRequest req, DepositService svc, CancellationToken ct) => (await svc.VoidSettlementAsync(leaseId, req, ct)).ToResult());
    }
}
