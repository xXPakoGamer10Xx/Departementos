using Habita.Api.Auth;
using Habita.Application.Access;

namespace Habita.Api.Endpoints;

public static class AccessCodeEndpoints
{
    /// <summary>Owner and Admin hand out short single-use codes. Redeeming one is public and lives under /auth.</summary>
    public static void MapAccessCodeEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/access-codes").RequireAuthorization(Policies.ManageUsers);
        g.MapGet("", async (AccessCodeService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        g.MapPost("", async (CreateAccessCodeRequest req, AccessCodeService svc, CancellationToken ct) =>
            (await svc.CreateAsync(req, ct)).ToResult(c => Results.Created($"/api/v1/access-codes/{c.Id}", c)));
        g.MapPost("/{id:guid}/revoke", async (Guid id, AccessCodeService svc, CancellationToken ct) => (await svc.RevokeAsync(id, ct)).ToResult());
    }
}
