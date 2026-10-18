using Habita.Api.Auth;
using Habita.Application.Members;
using Habita.Domain.Organizations;
using Habita.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Habita.Application.Abstractions;

namespace Habita.Api.Endpoints;

public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/members").RequireAuthorization(Policies.ManageUsers);
        g.MapGet("", async (MemberService svc, UserManager<ApplicationUser> users, CancellationToken ct) =>
        {
            var members = await svc.ListAsync(ct);
            var ids = members.Select(m => m.UserId).ToList();
            var profiles = await users.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
            return Results.Ok(members.Select(m => new
            {
                m.Id, m.UserId, m.Role, m.Status, m.TenantId,
                email = profiles.GetValueOrDefault(m.UserId)?.Email,
                fullName = profiles.GetValueOrDefault(m.UserId)?.FullName,
            }));
        });
        g.MapPost("/invitations", Invite);
        g.MapPatch("/{id:guid}", async (Guid id, UpdateMemberRequest req, MemberService svc, CancellationToken ct) =>
            (await svc.UpdateAsync(id, req, ct)).ToResult());
    }

    static async Task<IResult> Invite(InviteRequest req, MemberService svc, IEmailSender mail, IConfiguration config, CancellationToken ct)
    {
        var r = await svc.InviteAsync(req, ct);
        if (r.Outcome != Outcome.Ok) return r.ToResult();
        var link = $"{config["Web:BaseUrl"] ?? "http://localhost:3000"}/invitations/accept?token={r.Value!.Token}";
        await mail.SendAsync(req.Email.Trim(), "Invitación a Habita", $"Te invitaron a una organización en Habita. Acepta aquí: {link}");
        // The raw token is only returned when explicitly enabled (local dev / tests).
        return Results.Created($"/api/v1/members/invitations/{r.Value.InvitationId}",
            config.GetValue<bool>("Auth:ExposeInvitationTokens") ? new { id = r.Value.InvitationId, token = r.Value.Token } : new { id = r.Value.InvitationId });
    }
}
