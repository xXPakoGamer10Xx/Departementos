using Habita.Application.Abstractions;
using Habita.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;

namespace Habita.Api.Auth;

public static class Policies
{
    /// <summary>Anyone who works inside the organization. Excludes the Tenant (portal) login and the Collector, who only get the endpoints they need.</summary>
    public const string Staff = nameof(Staff);
    /// <summary>Any active member of the organization, tenant portal login included. Only for endpoints that check ownership themselves (files, /portal).</summary>
    public const string AnyMember = nameof(AnyMember);
    /// <summary>The tenant portal login, and nobody else.</summary>
    public const string TenantOnly = nameof(TenantOnly);
    public const string ManageUsers = nameof(ManageUsers);
    public const string ManageProperties = nameof(ManageProperties);
    public const string ReadTenants = nameof(ReadTenants);
    public const string ReadLeases = nameof(ReadLeases);
    public const string ReadFinance = nameof(ReadFinance);
    public const string WriteFinance = nameof(WriteFinance);
    public const string ReadReports = nameof(ReadReports);
    public const string WritePayments = nameof(WritePayments);
    public const string OperateIssues = nameof(OperateIssues);

    public static void AddHabitaPolicies(this AuthorizationOptions o)
    {
        o.AddPolicy(TenantOnly, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Tenant)));
        o.AddPolicy(AnyMember, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement()));
        o.AddPolicy(Staff, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance, Role.Maintenance, Role.ReadOnly)));
        o.AddPolicy(ManageProperties, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin)));
        // Tenants hold personal data: Maintenance has no access (privacy minimization, spec §3.8).
        // The Collector also sees tenants (to find who is paying) but with contact notes withheld, see TenantService.
        o.AddPolicy(ReadTenants, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance, Role.ReadOnly, Role.Collector)));
        // Leases carry rent amounts and tenant identity: no access for Maintenance.
        o.AddPolicy(ReadLeases, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance, Role.ReadOnly)));
        // Financial data: Maintenance never sees it. Finance operates (creates charges, later payments); voiding stays with Owner/Admin.
        // Charges and payments: the Collector reads them to collect. Reports and exports are a separate, narrower policy.
        o.AddPolicy(ReadFinance, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance, Role.ReadOnly, Role.Collector)));
        o.AddPolicy(ReadReports, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance, Role.ReadOnly)));
        // Recording a payment is also what a Collector does; creating charges is not (WriteFinance).
        o.AddPolicy(WritePayments, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance, Role.Collector)));
        o.AddPolicy(WriteFinance, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Finance)));
        // Status changes and comments on maintenance issues: Maintenance (only on its own, checked in the service), Owner and Admin.
        o.AddPolicy(OperateIssues, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin, Role.Maintenance)));
        o.AddPolicy(ManageUsers, p => p.RequireAuthenticatedUser().AddRequirements(new RoleRequirement(Role.Owner, Role.Admin)));
    }
}

public class RoleRequirement(params Role[] roles) : IAuthorizationRequirement
{
    public Role[] Roles { get; } = roles;
}

public class RoleHandler(ICurrentOrganization org) : AuthorizationHandler<RoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext ctx, RoleRequirement req)
    {
        if (org.Role is { } role && (req.Roles.Length == 0 || req.Roles.Contains(role))) ctx.Succeed(req);
        return Task.CompletedTask;
    }
}
