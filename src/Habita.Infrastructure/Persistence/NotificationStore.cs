using Habita.Application.Abstractions;
using Habita.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Persistence;

/// <summary>
/// Raw SQL for the two things EF cannot say: "insert unless it is already there" and "take these rows, skipping the ones another worker holds".
/// Raw SQL bypasses the tenant filter and interceptor, so every statement restricts by the organization of the current scope.
/// </summary>
public class NotificationStore(HabitaDbContext db, ICurrentOrganization org) : INotificationStore
{
    private Guid OrgId => org.Id ?? throw new InvalidOperationException("No hay organización activa.");

    public async Task<int> InsertIgnoringDuplicatesAsync(IReadOnlyList<Notification> items, CancellationToken ct)
    {
        var orgId = OrgId; var inserted = 0;
        foreach (var n in items)
        {
            var type = n.Type.ToString();
            inserted += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Notifications" ("Id","OrganizationId","UserId","Type","Title","Body","Url","DedupKey","CreatedAt","DeliveryAttempts")
                VALUES ({n.Id}, {orgId}, {n.UserId}, {type}, {n.Title}, {n.Body}, {n.Url}, {n.DedupKey}, {n.CreatedAt}, 0)
                ON CONFLICT ("OrganizationId","UserId","DedupKey") WHERE "DedupKey" IS NOT NULL DO NOTHING
                """, ct);
        }
        return inserted;
    }

    public async Task<List<Notification>> LockPendingAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var orgId = OrgId;
        return await db.Notifications.FromSqlInterpolated($"""
            SELECT * FROM "Notifications"
            WHERE "OrganizationId" = {orgId} AND "DeliveredAt" IS NULL AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now})
            ORDER BY "CreatedAt" LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).AsTracking().ToListAsync(ct);
    }

    public Task<List<Guid>> OrganizationsWithPendingAsync(DateTimeOffset now, CancellationToken ct) =>
        db.Notifications.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.DeliveredAt == null && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
            .Select(n => n.OrganizationId).Distinct().ToListAsync(ct);
}
