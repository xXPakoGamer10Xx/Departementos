using Habita.Api.Auth;
using Habita.Application.Maintenance;
using Habita.Domain.Maintenance;

namespace Habita.Api.Endpoints;

public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this RouteGroupBuilder v1)
    {
        var read = v1.MapGroup("/maintenance-issues").RequireAuthorization(Policies.Staff);   // every role
        var operate = v1.MapGroup("/maintenance-issues").RequireAuthorization(Policies.OperateIssues); // Owner, Admin, Maintenance (own issues)
        var manage = v1.MapGroup("/maintenance-issues").RequireAuthorization(Policies.ManageProperties); // Owner, Admin

        read.MapGet("", async (MaintenanceService svc, IssueStatus? status, bool? activeOnly, IssuePriority? priority, Guid? propertyId, Guid? unitId, Guid? assignedToUserId,
            bool? assignedToMe, string? search, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(status, activeOnly ?? false, priority, propertyId, unitId, assignedToUserId, assignedToMe ?? false, search, page ?? 1, pageSize ?? 25, ct)));
        read.MapGet("/{id:guid}", async (Guid id, MaintenanceService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

        manage.MapPost("", async (CreateIssueRequest req, MaintenanceService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(i => Results.Created($"/api/v1/maintenance-issues/{i.Id}", i)));
        manage.MapPatch("/{id:guid}", async (Guid id, UpdateIssueRequest req, MaintenanceService svc, CancellationToken ct) => (await svc.UpdateAsync(id, req, ct)).ToResult());
        manage.MapPost("/{id:guid}/assign", async (Guid id, AssignIssueRequest req, MaintenanceService svc, CancellationToken ct) => (await svc.AssignAsync(id, req, ct)).ToResult());

        operate.MapPost("/{id:guid}/status", async (Guid id, ChangeIssueStatusRequest req, MaintenanceService svc, CancellationToken ct) => (await svc.ChangeStatusAsync(id, req, ct)).ToResult());
        operate.MapPost("/{id:guid}/comments", async (Guid id, CommentIssueRequest req, MaintenanceService svc, CancellationToken ct) => (await svc.CommentAsync(id, req, ct)).ToResult());
    }
}
