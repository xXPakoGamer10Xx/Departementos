using System.Security.Claims;
using System.Text.RegularExpressions;
using Habita.Api.Auth;
using Habita.Application.Abstractions;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Identity;
using Habita.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Habita.Api.Endpoints;

public record RegisterRequest(string Email, string Password, string FullName, string OrganizationName);
public record LoginRequest(string Email, string Password);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string Token, string NewPassword);
public record AcceptInvitationRequest(string Token, string? FullName, string? Password);
public record RedeemAccessCodeRequest(string Code, string? Email, string? FullName, string? Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/auth");
        // Per-IP limits (see Security/RateLimiting). They are metadata only: when rate limiting is off the middleware is absent and they do nothing.
        g.MapPost("/register", Register).RequireRateLimiting(Habita.Api.Security.RateLimiting.Signup);
        g.MapPost("/login", Login).RequireRateLimiting(Habita.Api.Security.RateLimiting.Auth);
        g.MapPost("/logout", async (SignInManager<ApplicationUser> sm) => { await sm.SignOutAsync(); return Results.NoContent(); });
        g.MapPost("/forgot-password", ForgotPassword).RequireRateLimiting(Habita.Api.Security.RateLimiting.Signup);
        g.MapPost("/reset-password", ResetPassword).RequireRateLimiting(Habita.Api.Security.RateLimiting.Auth);
        g.MapPost("/invitations/accept", AcceptInvitation).RequireRateLimiting(Habita.Api.Security.RateLimiting.Auth);
        g.MapPost("/access-codes/redeem", RedeemAccessCode).RequireRateLimiting(Habita.Api.Security.RateLimiting.Auth);
    }

    static async Task<IResult> Register(RegisterRequest req, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> sm,
        HabitaDbContext db, ICurrentOrganization current, HttpCurrentUser actor, IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.OrganizationName) || string.IsNullOrWhiteSpace(req.FullName))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Nombre y organización son obligatorios."] });

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new ApplicationUser { UserName = req.Email.Trim(), Email = req.Email.Trim(), FullName = req.FullName.Trim() };
        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded) return IdentityProblem(created);

        var org = new Organization { Name = req.OrganizationName.Trim(), Slug = await UniqueSlug(db, req.OrganizationName, ct), CreatedAt = clock.UtcNow };
        db.Organizations.Add(org);
        current.Set(org.Id, Role.Owner, org.Status);
        actor.Override = user.Id;
        var membership = new Membership { OrganizationId = org.Id, UserId = user.Id, Role = Role.Owner, CreatedAt = clock.UtcNow };
        db.Memberships.Add(membership);
        audit.Write("organization.created", nameof(Organization), org.Id, metadata: new { org.Name, org.Slug });
        audit.Write("membership.created", nameof(Membership), membership.Id, metadata: new { membership.Role });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await SignIn(sm, user, org.Id);
        return Results.Created($"/api/v1/organizations/{org.Id}", new { organizationId = org.Id, slug = org.Slug });
    }

    static async Task<IResult> Login(LoginRequest req, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> sm,
        HabitaDbContext db, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        var result = user is null ? null : await sm.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (user is null || result is null || !result.Succeeded)
            return Results.Problem("Credenciales inválidas.", statusCode: result?.IsLockedOut == true ? 423 : 401);

        var orgIds = await db.Memberships.IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Active)
            .Select(m => m.OrganizationId).ToListAsync(ct);
        await SignIn(sm, user, orgIds.Count == 1 ? orgIds[0] : null);
        return Results.NoContent();
    }

    static async Task<IResult> ForgotPassword(ForgotPasswordRequest req, UserManager<ApplicationUser> users, IEmailSender mail,
        IConfiguration config)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is not null)
        {
            var token = Uri.EscapeDataString(await users.GeneratePasswordResetTokenAsync(user));
            var link = $"{config["Web:BaseUrl"] ?? "http://localhost:3000"}/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={token}";
            await mail.SendAsync(user.Email!, "Restablecer contraseña de Habita", $"Usa este enlace para restablecer tu contraseña: {link}");
        }
        return Results.Accepted(); // same response whether or not the account exists
    }

    static async Task<IResult> ResetPassword(ResetPasswordRequest req, UserManager<ApplicationUser> users)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null) return Results.Problem("Token inválido o expirado.", statusCode: 400);
        var result = await users.ResetPasswordAsync(user, req.Token, req.NewPassword);
        return result.Succeeded ? Results.NoContent() : IdentityProblem(result);
    }

    static async Task<IResult> AcceptInvitation(AcceptInvitationRequest req, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> sm, HabitaDbContext db, ICurrentOrganization current, HttpCurrentUser actor,
        IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        var hash = Habita.Application.Members.MemberService.HashToken(req.Token);
        var inv = await db.Invitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (inv is null || inv.AcceptedAt is not null || inv.ExpiresAt < clock.UtcNow)
            return Results.Problem("Invitación inválida o expirada.", statusCode: 400);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await users.FindByEmailAsync(inv.Email.ToLowerInvariant());
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.FullName))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["account"] = ["Nombre y contraseña son obligatorios para crear la cuenta."] });
            user = new ApplicationUser { UserName = inv.Email.ToLowerInvariant(), Email = inv.Email.ToLowerInvariant(), FullName = req.FullName.Trim(), EmailConfirmed = true };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return IdentityProblem(created);
        }
        else
        {
            var signedIn = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;
            if (signedIn is null) return Results.Problem("Inicia sesión con la cuenta invitada para aceptar.", statusCode: 401);
            if (signedIn != user.Id) return Results.Problem("La invitación es para otra cuenta.", statusCode: 403);
        }

        var exists = await db.Memberships.IgnoreQueryFilters().AnyAsync(m => m.OrganizationId == inv.OrganizationId && m.UserId == user.Id, ct);
        if (exists) return Results.Problem("Ya perteneces a esta organización.", statusCode: 409);

        var org = await db.Organizations.FirstAsync(o => o.Id == inv.OrganizationId, ct);
        current.Set(org.Id, inv.Role, org.Status);
        actor.Override = user.Id;
        var membership = new Membership { OrganizationId = org.Id, UserId = user.Id, Role = inv.Role, CreatedAt = clock.UtcNow };
        db.Memberships.Add(membership);
        inv.AcceptedAt = clock.UtcNow;
        audit.Write("invitation.accepted", nameof(Invitation), inv.Id);
        audit.Write("membership.created", nameof(Membership), membership.Id, metadata: new { membership.Role });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await SignIn(sm, user, org.Id);
        return Results.NoContent();
    }

    static async Task<IResult> RedeemAccessCode(RedeemAccessCodeRequest req, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> sm, HabitaDbContext db, ICurrentOrganization current, HttpCurrentUser actor,
        IAuditWriter audit, IClock clock, CancellationToken ct)
    {
        const string invalid = "Código inválido o vencido.";
        var normalized = AccessCode.Normalize(req.Code);
        if (normalized is null) return Results.Problem(invalid, statusCode: 400);
        var hash = AccessCode.Hash(normalized);
        var now = clock.UtcNow;
        var code = await db.AccessCodes.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (code is null || !code.IsUsable(now)) return Results.Problem(invalid, statusCode: 400); // same answer for unknown, used, revoked and expired

        var signedIn = Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : (Guid?)null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        ApplicationUser? user;
        if (signedIn is { } id) user = await users.FindByIdAsync(id.ToString());
        else
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.FullName))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["account"] = ["Nombre, correo y contraseña son obligatorios para crear la cuenta."] });
            var email = req.Email.Trim().ToLowerInvariant();
            if (await users.FindByEmailAsync(email) is not null)
                return Results.Problem("Ese correo ya tiene cuenta. Inicia sesión y vuelve a usar el código.", statusCode: 401);
            user = new ApplicationUser { UserName = email, Email = email, FullName = req.FullName.Trim() };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return IdentityProblem(created);
        }
        if (user is null) return Results.Unauthorized();

        if (await db.Memberships.IgnoreQueryFilters().AnyAsync(m => m.OrganizationId == code.OrganizationId && m.UserId == user.Id, ct))
            return Results.Problem("Ya perteneces a esta organización.", statusCode: 409);

        // Single use, decided by the database: of two simultaneous redemptions exactly one updates a row.
        var consumed = await db.AccessCodes.IgnoreQueryFilters()
            .Where(c => c.Id == code.Id && c.UsedAt == null && c.RevokedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now).SetProperty(c => c.UsedByUserId, user.Id), ct);
        if (consumed != 1) return Results.Problem(invalid, statusCode: 400);

        var org = await db.Organizations.FirstAsync(o => o.Id == code.OrganizationId, ct);
        current.Set(org.Id, code.Role, org.Status, code.TenantId);
        actor.Override = user.Id;
        // A tenant record has one login at a time. If its previous login was deactivated, the new account takes that membership over
        // (the old account loses access); an active one cannot be taken (the code could not have been created).
        var previous = code.TenantId is { } tenantRecord
            ? await db.Memberships.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.OrganizationId == code.OrganizationId && m.TenantId == tenantRecord, ct)
            : null;
        if (previous is { Status: MembershipStatus.Active }) return Results.Problem("Este inquilino ya tiene un acceso activo.", statusCode: 409);
        Membership membership;
        if (previous is not null)
        {
            membership = previous;
            membership.UserId = user.Id; membership.Status = MembershipStatus.Active; membership.CreatedAt = now;
            audit.Write("access_code.redeemed", nameof(AccessCode), code.Id, metadata: new { code.Role, replacedPreviousLogin = true });
            audit.Write("membership.updated", nameof(Membership), membership.Id, metadata: new { reassigned = true });
        }
        else
        {
            membership = new Membership { OrganizationId = org.Id, UserId = user.Id, Role = code.Role, TenantId = code.TenantId, CreatedAt = now };
            db.Memberships.Add(membership);
            audit.Write("access_code.redeemed", nameof(AccessCode), code.Id, metadata: new { code.Role });
            audit.Write("membership.created", nameof(Membership), membership.Id, metadata: new { membership.Role });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Results.Problem("Este acceso ya fue tomado.", statusCode: 409); } // unique (org, tenant): one login per tenant record
        await tx.CommitAsync(ct);

        await SignIn(sm, user, org.Id);
        return Results.NoContent();
    }

    internal static Task SignIn(SignInManager<ApplicationUser> sm, ApplicationUser user, Guid? orgId) =>
        sm.SignInWithClaimsAsync(user, isPersistent: false, orgId is null ? [] : [new Claim(Claims.OrgId, orgId.Value.ToString())]);

    static IResult IdentityProblem(IdentityResult r) =>
        Results.ValidationProblem(r.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));

    static async Task<string> UniqueSlug(HabitaDbContext db, string name, CancellationToken ct)
    {
        var baseSlug = Regex.Replace(name.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD), @"[^a-z0-9]+", "-").Trim('-');
        if (baseSlug.Length == 0) baseSlug = "org";
        var slug = baseSlug;
        for (var i = 2; await db.Organizations.AnyAsync(o => o.Slug == slug, ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }
}
