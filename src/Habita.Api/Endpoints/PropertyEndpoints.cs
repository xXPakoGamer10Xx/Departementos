using Habita.Api.Auth;
using Habita.Application.Properties;
using Habita.Domain.Properties;

namespace Habita.Api.Endpoints;

public static class PropertyEndpoints
{
    public static void MapPropertyEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/properties").RequireAuthorization(Policies.Staff);
        v1.MapGet("/units", async (PropertyService svc, string? search, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.SearchUnitsAsync(search, page ?? 1, pageSize ?? 20, ct))).RequireAuthorization(Policies.Staff);
        var write = v1.MapGroup("/properties").RequireAuthorization(Policies.ManageProperties);

        read.MapGet("", async (PropertyService svc, string? search, PropertyStatus? status, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListPropertiesAsync(search, status, page ?? 1, pageSize ?? 25, ct)));
        read.MapGet("/{id:guid}", async (Guid id, PropertyService svc, CancellationToken ct) => (await svc.GetPropertyAsync(id, ct)).ToResult());
        read.MapGet("/{id:guid}/units", async (Guid id, PropertyService svc, string? search, UnitStatus? status, int? page, int? pageSize, CancellationToken ct) =>
            (await svc.ListUnitsAsync(id, search, status, page ?? 1, pageSize ?? 50, ct)).ToResult());

        write.MapPost("", async (CreatePropertyRequest req, PropertyService svc, CancellationToken ct) =>
            (await svc.CreatePropertyAsync(req, ct)).ToResult(p => Results.Created($"/api/v1/properties/{p.Id}", p)));
        write.MapPatch("/{id:guid}", async (Guid id, UpdatePropertyRequest req, PropertyService svc, CancellationToken ct) =>
            (await svc.UpdatePropertyAsync(id, req, ct)).ToResult());
        write.MapPost("/{id:guid}/units", async (Guid id, CreateUnitRequest req, PropertyService svc, CancellationToken ct) =>
            (await svc.CreateUnitAsync(id, req, ct)).ToResult(u => Results.Created($"/api/v1/properties/{id}/units/{u.Id}", u)));
        write.MapPatch("/{id:guid}/units/{unitId:guid}", async (Guid id, Guid unitId, UpdateUnitRequest req, PropertyService svc, CancellationToken ct) =>
            (await svc.UpdateUnitAsync(id, unitId, req, ct)).ToResult());
    }
}
