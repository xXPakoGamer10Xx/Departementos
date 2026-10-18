using System.Security.Claims;
using Habita.Api.Auth;
using Habita.Application.Abstractions;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Identity;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Habita.Api.Endpoints;

public record SwitchOrganizationRequest(Guid OrganizationId);

public static class MeEndpoints
{
    public static void MapMeEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/me", Me).RequireAuthorization();
        v1.MapPost("/me/active-organization", SwitchOrganization).RequireAuthorization();
    }

    static async Task<IResult> Me(ClaimsPrincipal principal, ICurrentOrganization current, HabitaDbContext db, UserManager<ApplicationUser> users, CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null) return Results.Unauthorized();
        // Explicit cross-organization read: only the caller's own memberships.
        var orgs = await db.Memberships.IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .Join(db.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { o.Id, o.Name, o.Slug, o.Currency, m.Role })
            .ToListAsync(ct);
        return Results.Ok(new
        {
            user = new { user.Id, user.Email, user.FullName },
            activeOrganizationId = current.Id,
            activeRole = current.Role,
            activeTenantId = current.TenantId,
            organizations = orgs,
        });
    }

    static async Task<IResult> SwitchOrganization(SwitchOrganizationRequest req, ClaimsPrincipal principal, HabitaDbContext db,
        UserManager<ApplicationUser> users, SignInManager<ApplicationUser> sm, CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var ok = await db.Memberships.IgnoreQueryFilters()
            .AnyAsync(m => m.UserId == userId && m.OrganizationId == req.OrganizationId && m.Status == MembershipStatus.Active, ct);
        if (!ok) return Results.NotFound(); // do not reveal whether the organization exists
        var user = await users.FindByIdAsync(userId.ToString());
        await AuthEndpoints.SignIn(sm, user!, req.OrganizationId);
        return Results.NoContent();
    }
}
