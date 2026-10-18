using System.Security.Claims;
using Habita.Application.Abstractions;

namespace Habita.Api.Auth;

public static class Claims
{
    public const string OrgId = "org_id";
}

public class HttpCurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    /// <summary>Set by flows that act before a cookie exists (register, accept-invitation).</summary>
    public Guid? Override { get; set; }

    public Guid? UserId => Override ??
        (Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null);
}
