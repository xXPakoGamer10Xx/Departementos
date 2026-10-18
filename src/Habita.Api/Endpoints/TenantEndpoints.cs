using Habita.Api.Auth;
using Habita.Application.Tenants;
using Habita.Domain.Tenants;

namespace Habita.Api.Endpoints;

public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/tenants").RequireAuthorization(Policies.ReadTenants);
        var write = v1.MapGroup("/tenants").RequireAuthorization(Policies.ManageProperties); // Owner/Admin

        read.MapGet("", async (TenantService svc, string? search, TenantStatus? status, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(search, status, page ?? 1, pageSize ?? 25, ct)));
        read.MapGet("/{id:guid}", async (Guid id, TenantService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

        write.MapPost("", async (CreateTenantRequest req, TenantService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(t => Results.Created($"/api/v1/tenants/{t.Id}", t)));
        write.MapPatch("/{id:guid}", async (Guid id, UpdateTenantRequest req, TenantService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(id, req, ct)).ToResult());
    }
}
