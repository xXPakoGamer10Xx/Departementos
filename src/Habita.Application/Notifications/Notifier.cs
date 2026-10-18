using Habita.Application.Abstractions;
using Habita.Domain.Notifications;
using Habita.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Notifications;

/// <summary>Which staff roles hear about something. Collector and Maintenance are never in: they work from their own screens.</summary>
public enum Audience { Reviewers, Managers }

/// <summary>
/// Stages notifications in the caller's unit of work, so a notification exists if and only if the event it reports was saved.
/// Nothing is sent from here: email and push go out later (see <see cref="NotificationDispatcher"/>).
/// </summary>
public class Notifier(IAppDbContext db, IClock clock)
{
    public static readonly Role[] ReviewerRoles = [Role.Owner, Role.Admin, Role.Finance];
    public static readonly Role[] ManagerRoles = [Role.Owner, Role.Admin];

    public static Role[] RolesFor(Audience a) => a == Audience.Managers ? ManagerRoles : ReviewerRoles;

    public async Task ToStaffAsync(Audience audience, NotificationMessage message, CancellationToken ct)
    {
        var roles = RolesFor(audience);
        var users = await db.Memberships.AsNoTracking().Where(m => roles.Contains(m.Role) && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync(ct);
        foreach (var user in users) db.Notifications.Add(Build(user, message, null, clock.UtcNow));
    }

    /// <summary>The login of this tenant record, if it has an active one. A tenant with no portal access simply gets nothing.</summary>
    public async Task ToTenantAsync(Guid tenantId, NotificationMessage message, CancellationToken ct)
    {
        var user = await db.Memberships.AsNoTracking().Where(m => m.Role == Role.Tenant && m.TenantId == tenantId && m.Status == MembershipStatus.Active)
            .Select(m => (Guid?)m.UserId).FirstOrDefaultAsync(ct);
        if (user is { } u) db.Notifications.Add(Build(u, message, null, clock.UtcNow));
    }

    public static Notification Build(Guid userId, NotificationMessage m, string? dedupKey, DateTimeOffset now)
    {
        if (Notification.Validate(m.Title, m.Body, m.Url) is { } problem) throw new InvalidOperationException($"Notificación inválida ({m.Type}): {problem}");
        return new Notification { UserId = userId, Type = m.Type, Title = m.Title, Body = m.Body, Url = m.Url, DedupKey = dedupKey, CreatedAt = now };
    }
}
