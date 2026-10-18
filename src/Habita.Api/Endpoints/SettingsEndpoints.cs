using Habita.Api.Auth;
using Habita.Application.Settings;

namespace Habita.Api.Endpoints;

public static class SettingsEndpoints
{
    /// <summary>Owner and Admin only: the landlord identity and reminder schedule printed on contracts and sent to tenants.</summary>
    public static void MapSettingsEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/settings").RequireAuthorization(Policies.ManageUsers);
        g.MapGet("", async (SettingsService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(ct)));
        g.MapPatch("", async (UpdateSettingsRequest req, SettingsService svc, CancellationToken ct) => (await svc.UpdateAsync(req, ct)).ToResult());
    }
}
