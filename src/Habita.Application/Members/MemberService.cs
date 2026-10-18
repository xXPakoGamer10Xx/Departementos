using System.Security.Cryptography;
using Habita.Application.Abstractions;
using Habita.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Members;

public record MemberDto(Guid Id, Guid UserId, Role Role, MembershipStatus Status, Guid? TenantId = null);
public record InviteRequest(string Email, Role Role);
public record UpdateMemberRequest(Role? Role, MembershipStatus? Status);
public record InviteResult(Guid InvitationId, string Token);

public enum Outcome { Ok, NotFound, Forbidden, Invalid, Conflict, Unavailable }
public record Result<T>(Outcome Outcome, T? Value = default, string? Error = null);

public class MemberService(IAppDbContext db, ICurrentOrganization org, ICurrentUser user, IAuditWriter audit, IClock clock)
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public async Task<List<MemberDto>> ListAsync(CancellationToken ct) =>
        await db.Memberships.OrderBy(m => m.CreatedAt)
            .Select(m => new MemberDto(m.Id, m.UserId, m.Role, m.Status, m.TenantId)).ToListAsync(ct);

    public async Task<Result<InviteResult>> InviteAsync(InviteRequest req, CancellationToken ct)
    {
        if (!Membership.CanAssign(org.Role!.Value, req.Role))
            return new(Outcome.Forbidden, Error: "No puedes asignar ese rol.");
        var email = req.Email.Trim().ToUpperInvariant();
        if (!email.Contains('@')) return new(Outcome.Invalid, Error: "Email inválido.");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var inv = new Invitation
        {
            OrganizationId = org.Id!.Value, Email = email, Role = req.Role,
            TokenHash = HashToken(token), InvitedByUserId = user.UserId!.Value,
            CreatedAt = clock.UtcNow, ExpiresAt = clock.UtcNow + InvitationLifetime,
        };
        db.Invitations.Add(inv);
        audit.Write("invitation.created", nameof(Invitation), inv.Id, metadata: new { email, req.Role });
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, new InviteResult(inv.Id, token));
    }

    public async Task<Result<MemberDto>> UpdateAsync(Guid memberId, UpdateMemberRequest req, CancellationToken ct)
    {
        var m = await db.Memberships.FirstOrDefaultAsync(x => x.Id == memberId, ct);
        if (m is null) return new(Outcome.NotFound);
        var actor = org.Role!.Value;
        if (!Membership.CanManage(actor, m.Role)) return new(Outcome.Forbidden, Error: "No puedes modificar a este miembro.");
        if (req.Role is { } newRole && !Membership.CanAssign(actor, newRole))
            return new(Outcome.Forbidden, Error: "No puedes asignar ese rol.");

        if (m.Role == Role.Tenant && req.Role is { } r && r != Role.Tenant)
            return new(Outcome.Invalid, Error: "Un acceso de inquilino no cambia de rol. Desactívalo y da otro acceso.");
        var newRoleValue = req.Role ?? m.Role;
        var newStatus = req.Status ?? m.Status;
        var losesOwner = m.Role == Role.Owner && m.Status == MembershipStatus.Active
                         && (newRoleValue != Role.Owner || newStatus != MembershipStatus.Active);
        if (losesOwner)
        {
            var otherOwners = await db.Memberships.CountAsync(x => x.Id != m.Id && x.Role == Role.Owner && x.Status == MembershipStatus.Active, ct);
            if (otherOwners == 0) return new(Outcome.Invalid, Error: "La organización debe conservar al menos un Owner activo.");
        }

        var before = new { m.Role, m.Status };
        m.Role = newRoleValue; m.Status = newStatus;
        audit.Write("membership.updated", nameof(Membership), m.Id, metadata: new { before, after = new { m.Role, m.Status } });
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, new MemberDto(m.Id, m.UserId, m.Role, m.Status, m.TenantId));
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
