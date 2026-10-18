using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Organizations;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Access;

public record CreateAccessCodeRequest(Role Role, Guid? TenantId, int? ValidDays);
/// <summary>The only time the plain code is returned.</summary>
public record AccessCodeCreated(Guid Id, string Code, Role Role, Guid? TenantId, DateTimeOffset ExpiresAt);
public record AccessCodeDto(Guid Id, Role Role, Guid? TenantId, string? TenantName, string Status, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public class AccessCodeService(IAppDbContext db, ICurrentOrganization org, ICurrentUser user, IAuditWriter audit, IClock clock)
{
    public async Task<Result<AccessCodeCreated>> CreateAsync(CreateAccessCodeRequest req, CancellationToken ct)
    {
        if (!AccessCode.IsGrantable(req.Role))
            return new(Outcome.Invalid, Error: "Un código solo puede dar acceso de Cobrador o de Inquilino.");
        var days = req.ValidDays ?? (int)AccessCode.DefaultLifetime.TotalDays;
        if (days < 1 || days > AccessCode.MaxLifetime.TotalDays) return new(Outcome.Invalid, Error: $"La vigencia va de 1 a {(int)AccessCode.MaxLifetime.TotalDays} días.");

        Guid? tenantId = null;
        if (req.Role == Role.Tenant)
        {
            if (req.TenantId is not { } tid) return new(Outcome.Invalid, Error: "El código de inquilino necesita un inquilino.");
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tid, ct);
            if (tenant is null) return new(Outcome.NotFound);
            if (tenant.Status != TenantStatus.Active) return new(Outcome.Invalid, Error: "El inquilino está archivado.");
            // A deactivated login does not block a new one: redeeming hands that membership to the new account.
            if (await db.Memberships.AnyAsync(m => m.TenantId == tid && m.Status == MembershipStatus.Active, ct))
                return new(Outcome.Conflict, Error: "Este inquilino ya tiene un acceso activo. Desactívalo antes de dar otro.");
            tenantId = tid;
        }
        else if (req.TenantId is not null) return new(Outcome.Invalid, Error: "Un código de Cobrador no se liga a un inquilino.");

        var now = clock.UtcNow;
        if (tenantId is { } t) // one live code per tenant: a new one replaces the old
            foreach (var old in await db.AccessCodes.Where(c => c.TenantId == t && c.UsedAt == null && c.RevokedAt == null).ToListAsync(ct))
            {
                old.RevokedAt = now;
                audit.Write("access_code.revoked", nameof(AccessCode), old.Id, reason: "Reemplazado por un código nuevo.");
            }

        var code = AccessCode.Generate();
        var row = new AccessCode
        {
            OrganizationId = org.Id!.Value, CodeHash = AccessCode.Hash(code), Role = req.Role, TenantId = tenantId,
            ExpiresAt = now.AddDays(days), CreatedByUserId = user.UserId!.Value, CreatedAt = now,
        };
        db.AccessCodes.Add(row);
        audit.Write("access_code.created", nameof(AccessCode), row.Id, metadata: new { req.Role, tenantId, days }); // never the code
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, new AccessCodeCreated(row.Id, AccessCode.Format(code), row.Role, row.TenantId, row.ExpiresAt));
    }

    public async Task<List<AccessCodeDto>> ListAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.AccessCodes.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(100)
            .GroupJoin(db.Tenants, c => c.TenantId, t => t.Id, (c, ts) => new { c, ts })
            .SelectMany(x => x.ts.DefaultIfEmpty(), (x, t) => new { x.c, TenantName = t == null ? null : t.FullName })
            .ToListAsync(ct);
        return rows.Select(r => new AccessCodeDto(r.c.Id, r.c.Role, r.c.TenantId, r.TenantName,
            r.c.UsedAt is not null ? "Used" : r.c.RevokedAt is not null ? "Revoked" : r.c.ExpiresAt <= now ? "Expired" : "Pending",
            r.c.ExpiresAt, r.c.CreatedAt)).ToList();
    }

    public async Task<Result<AccessCodeDto>> RevokeAsync(Guid id, CancellationToken ct)
    {
        var c = await db.AccessCodes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return new(Outcome.NotFound);
        if (c.UsedAt is not null) return new(Outcome.Conflict, Error: "El código ya se usó.");
        if (c.RevokedAt is null)
        {
            c.RevokedAt = clock.UtcNow;
            audit.Write("access_code.revoked", nameof(AccessCode), c.Id);
            await db.SaveChangesAsync(ct);
        }
        return new(Outcome.Ok, new AccessCodeDto(c.Id, c.Role, c.TenantId, null, "Revoked", c.ExpiresAt, c.CreatedAt));
    }
}
