using System.Security.Claims;
using Habita.Application.Abstractions;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Habita.Api.Auth;

/// <summary>
/// Resolves the active organization from the authenticated identity (never from the request body),
/// re-validating on every request that the membership and organization are still active.
/// </summary>
public class OrganizationContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, HabitaDbContext db, ICurrentOrganization current)
    {
        var principal = ctx.User;
        if (principal.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && Guid.TryParse(principal.FindFirstValue(Claims.OrgId), out var orgId))
        {
            var row = await db.Memberships.IgnoreQueryFilters()
                .Where(m => m.UserId == userId && m.OrganizationId == orgId && m.Status == MembershipStatus.Active)
                .Join(db.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { m.Role, m.TenantId, o.Status })
                .FirstOrDefaultAsync(ctx.RequestAborted);

            if (row is not null)
            {
                current.Set(orgId, row.Role, row.Status, row.TenantId);
                if (row.Status == OrganizationStatus.Suspended && !HttpMethods.IsGet(ctx.Request.Method)
                    && !HttpMethods.IsHead(ctx.Request.Method)
                    && !ctx.Request.Path.StartsWithSegments("/api/v1/auth")
                    && !ctx.Request.Path.StartsWithSegments("/api/v1/me"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { title = "Organización suspendida", status = 403 });
                    return;
                }
            }
        }
        await next(ctx);
    }
}
