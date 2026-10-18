using Habita.Application.Abstractions;
using Habita.Application.Collections;
using Habita.Domain.Charges;
using Habita.Domain.Collections;
using Habita.Domain.Leases;
using Habita.Domain.Notifications;
using Habita.Domain.Organizations;
using Habita.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Notifications;

public record ReminderRun(bool Ran, int Created);

/// <summary>
/// The daily reminders of one organization: rent about to fall due and overdue charges, contracts about to end, and payment promises coming due.
/// It runs once per local day, from the organization's reminder hour, under a database lock so that several workers never run it twice at once,
/// and every notification carries a dedup key so even a repeated run stores nothing twice.
/// </summary>
public class ReminderService(IAppDbContext db, ICurrentOrganization org, IClock clock, OrgCalendar calendar, IDbTransactions tx, IFinanceLocks locks,
    INotificationStore store, PaymentPromiseService promises)
{
    /// <summary>Days after the due date on which an unpaid charge is reported again, to the tenant and to staff.</summary>
    public static readonly int[] OverdueDays = [1, 7, 14, 30];

    public Task<ReminderRun> RunIfDueAsync(CancellationToken ct) => tx.RunAsync(async () =>
    {
        if (!await locks.TryLockScopeAsync("reminders", ct)) return new ReminderRun(false, 0); // another worker is on it
        var settings = await db.Settings.FirstOrDefaultAsync(ct);
        var s = settings ?? new OrganizationSettings { OrganizationId = org.Id!.Value, UpdatedAt = clock.UtcNow };
        var local = await calendar.LocalNowAsync(ct);
        var today = DateOnly.FromDateTime(local);
        if (!OrganizationSettings.IsRunDue(s.ReminderHour, s.LastReminderRunOn, today, local.Hour)) return new ReminderRun(false, 0);

        var created = await BuildAsync(s, today, ct);
        s.LastReminderRunOn = today;
        if (settings is null) db.Settings.Add(s);
        await db.SaveChangesAsync(ct);
        return new ReminderRun(true, created);
    }, ct);

    /// <summary>Builds and stores today's reminders. Safe to call again: duplicates are ignored by their dedup key.</summary>
    public async Task<int> BuildAsync(OrganizationSettings settings, DateOnly today, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var tenantLogin = await db.Memberships.AsNoTracking().Where(m => m.Role == Role.Tenant && m.Status == MembershipStatus.Active && m.TenantId != null)
            .ToDictionaryAsync(m => m.TenantId!.Value, m => m.UserId, ct);
        var reviewers = await db.Memberships.AsNoTracking().Where(m => Notifier.ReviewerRoles.Contains(m.Role) && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync(ct);
        var managers = await db.Memberships.AsNoTracking().Where(m => Notifier.ManagerRoles.Contains(m.Role) && m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync(ct);
        var items = new List<Notification>();

        void ToTenant(Guid tenantId, NotificationMessage m, string key) { if (tenantLogin.TryGetValue(tenantId, out var u)) items.Add(Notifier.Build(u, m, key, now)); }
        void ToStaff(IEnumerable<Guid> who, NotificationMessage m, string key) { foreach (var u in who) items.Add(Notifier.Build(u, m, key, now)); }

        // ---- rent due soon (tenant) and overdue (tenant and staff) ----
        var dueDays = settings.RentReminderDays;
        // "Overdue" starts after the grace days, so the overdue steps count from the end of the grace period: with 3 grace days the first notice is 4 days after the due date.
        var grace = settings.GraceDays;
        var dueDates = dueDays.Select(today.AddDays).Concat(OverdueDays.Select(n => today.AddDays(-(grace + n)))).Distinct().ToList();
        if (dueDates.Count > 0)
        {
            var charges = await (from c in db.RentCharges.AsNoTracking()
                                 join l in db.Leases on c.LeaseId equals l.Id
                                 join t in db.Tenants on l.TenantId equals t.Id
                                 join u in db.Units on l.UnitId equals u.Id
                                 where dueDates.Contains(c.DueDate) && (c.Status == ChargeStatus.Open || c.Status == ChargeStatus.PartiallyPaid)
                                 select new { c.Id, c.Description, c.Amount, c.DueDate, c.Type, l.TenantId, t.FullName, UnitCode = u.Code,
                                              Allocated = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m }).ToListAsync(ct);
            foreach (var c in charges)
            {
                var balance = RentCharge.Balance(c.Amount, c.Allocated, voided: false);
                if (balance <= 0) continue;
                var daysLeft = c.DueDate.DayNumber - today.DayNumber;
                if (daysLeft >= 0 && dueDays.Contains(daysLeft))
                    ToTenant(c.TenantId, c.Type == ChargeType.Deposit ? NotificationText.DepositDueSoon(c.FullName, c.Description, balance, c.DueDate, daysLeft)
                                                                      : NotificationText.RentDueSoon(c.FullName, c.Description, balance, c.DueDate, daysLeft), $"rent-due:{c.Id}:{daysLeft}");
                else if (daysLeft < 0 && OverdueDays.Contains(-daysLeft - grace))
                {
                    var late = -daysLeft; // days since the due date, which is what people understand by "late"
                    ToTenant(c.TenantId, NotificationText.ChargeOverdueForTenant(c.FullName, c.Description, balance, late), $"overdue:{c.Id}:{late}");
                    ToStaff(reviewers, NotificationText.ChargeOverdueForStaff(c.FullName, c.Description, balance, late, c.UnitCode), $"overdue:{c.Id}:{late}");
                }
            }
        }

        // ---- contracts about to end ----
        var endDates = settings.ContractExpiryDays.Select(today.AddDays).Distinct().ToList();
        if (endDates.Count > 0)
        {
            var leases = await (from l in db.Leases.AsNoTracking()
                                join t in db.Tenants on l.TenantId equals t.Id
                                join u in db.Units on l.UnitId equals u.Id
                                where l.Status == LeaseStatus.Active && l.EndDate != null && endDates.Contains(l.EndDate!.Value)
                                select new { l.Id, l.TenantId, End = l.EndDate!.Value, t.FullName, UnitCode = u.Code }).ToListAsync(ct);
            foreach (var l in leases)
            {
                var daysLeft = l.End.DayNumber - today.DayNumber;
                var key = $"lease-expiring:{l.Id}:{daysLeft}";
                ToTenant(l.TenantId, NotificationText.LeaseExpiringForTenant(l.FullName, l.UnitCode, l.End, daysLeft), key);
                ToStaff(reviewers, NotificationText.LeaseExpiringForStaff(l.FullName, l.UnitCode, l.End, daysLeft, l.Id), key);
            }
        }

        // ---- promises: due today, or broken yesterday. They go to whoever recorded them, or to the managers if that person is gone. ----
        var active = reviewers.Concat(managers).ToHashSet();
        foreach (var p in await promises.OpenAroundAsync(today, ct))
        {
            var who = active.Contains(p.CreatedByUserId) ? [p.CreatedByUserId] : managers;
            if (p.Dto.Status == PromiseStatus.Pending && p.Dto.PromisedOn == today)
                ToStaff(who, NotificationText.PromiseDue(p.Dto.TenantName, p.Dto.ChargeDescription, p.Dto.ChargeBalance), $"promise-due:{p.Dto.Id}");
            else if (p.Dto.Status == PromiseStatus.Broken && p.Dto.PromisedOn == today.AddDays(-1))
                ToStaff(who, NotificationText.PromiseBroken(p.Dto.TenantName, p.Dto.ChargeDescription, p.Dto.ChargeBalance, p.Dto.PromisedOn), $"promise-broken:{p.Dto.Id}");
        }

        return items.Count == 0 ? 0 : await store.InsertIgnoringDuplicatesAsync(items, ct);
    }
}
