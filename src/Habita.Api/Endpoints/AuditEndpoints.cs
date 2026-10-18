using Habita.Api.Auth;
using Habita.Application.Audit;

namespace Habita.Api.Endpoints;

public static class AuditEndpoints
{
    /// <summary>Owner and Admin only. Read-only by design: no endpoint creates, edits or deletes audit entries.</summary>
    public static void MapAuditEndpoints(this RouteGroupBuilder v1) =>
        v1.MapGet("/audit-logs", async (AuditService svc, string? entityType, string? action, Guid? actorUserId, Guid? entityId, DateOnly? from, DateOnly? to,
                int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(entityType, action, actorUserId, entityId, from, to, page ?? 1, pageSize ?? 25, ct)))
            .RequireAuthorization(Policies.ManageUsers);
}
