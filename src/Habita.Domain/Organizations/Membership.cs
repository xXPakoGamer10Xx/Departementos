using Habita.Domain.Common;

namespace Habita.Domain.Organizations;

public class Membership : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Role Role { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    /// <summary>Only for <see cref="Role.Tenant"/>: the tenant record this login may see. Always null for staff roles.</summary>
    public Guid? TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Staff roles an actor may hand out. Tenant is never assignable by hand: it is only created by redeeming a code tied to a tenant record.</summary>
    public static bool CanAssign(Role actor, Role target) =>
        target != Role.Tenant && (actor == Role.Owner || (actor == Role.Admin && target != Role.Owner));

    public static bool CanManage(Role actor, Role current) =>
        actor == Role.Owner || (actor == Role.Admin && current != Role.Owner);

    /// <summary>Roles that work inside the organization (everything except the tenant portal login).</summary>
    public static bool IsStaff(Role role) => role != Role.Tenant;
}
