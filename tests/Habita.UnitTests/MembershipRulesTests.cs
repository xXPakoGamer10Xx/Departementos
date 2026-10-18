using Habita.Domain.Organizations;
using Xunit;

namespace Habita.UnitTests;

public class MembershipRulesTests
{
    [Theory]
    [InlineData(Role.Owner, Role.Owner, true)]
    [InlineData(Role.Owner, Role.Admin, true)]
    [InlineData(Role.Admin, Role.Owner, false)]
    [InlineData(Role.Admin, Role.Finance, true)]
    [InlineData(Role.Finance, Role.ReadOnly, false)]
    [InlineData(Role.Maintenance, Role.ReadOnly, false)]
    public void CanAssign_follows_role_hierarchy(Role actor, Role target, bool expected) =>
        Assert.Equal(expected, Membership.CanAssign(actor, target));

    [Theory]
    [InlineData(Role.Admin, Role.Owner, false)]
    [InlineData(Role.Admin, Role.Admin, true)]
    [InlineData(Role.Owner, Role.Owner, true)]
    [InlineData(Role.Finance, Role.Finance, false)]
    public void CanManage_protects_owners_from_admins(Role actor, Role current, bool expected) =>
        Assert.Equal(expected, Membership.CanManage(actor, current));
}
