using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Notifications;
using Habita.Application.Properties;
using Habita.Domain.Charges;
using Habita.Domain.Maintenance;
using Habita.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Portal;

public record PortalLeaseDto(Guid Id, string UnitCode, string PropertyName, string PropertyAddress, decimal MonthlyRent, int DueDay, DateOnly StartDate, DateOnly? EndDate);
public record PortalChargeDto(Guid Id, string Description, ChargeType Type, int? PeriodYear, int? PeriodMonth, decimal Amount, decimal Paid, decimal Balance, DateOnly DueDate, ChargeStatus Status);
public record PortalSummaryDto(string TenantName, string Currency, DateOnly Today, PortalLeaseDto? Lease, decimal TotalDue, decimal OverdueAmount, int OverdueCount, PortalChargeDto? NextCharge, decimal CreditBalance, bool HasContract = false);
public record PortalAllocationDto(string Description, decimal Amount);
/// <summary>A payment as the tenant sees it: no notes, no who-recorded-it, no reversal reason.</summary>
public record PortalPaymentDto(Guid Id, DateOnly ReceivedOn, decimal Amount, PaymentMethod Method, string? Reference, PaymentStatus Status, decimal Unassigned, IReadOnlyList<PortalAllocationDto> Allocations, string? ReceiptFolio = null);
/// <summary>FromTenant: the tenant wrote it. Otherwise it is the administration (staff names are never shown).</summary>
public record PortalIssueEventDto(IssueEventType Type, bool FromTenant, IssueStatus? ToStatus, string? Body, DateTimeOffset CreatedAt);
public record PortalIssueDto(Guid Id, string Title, string Description, IssueStatus Status, string? UnitCode, DateTimeOffset ReportedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt, IReadOnlyList<PortalIssueEventDto>? Events);
public record ReportIssueRequest(string Title, string? Description);
public record PortalCommentRequest(string? Body);

/// <summary>
/// Everything a tenant login may read or do. EVERY query starts from the tenant id of the signed-in membership (never from the request),
/// so there is no id a tenant can change to see someone else's data.
/// </summary>
public class PortalService(IAppDbContext db, ICurrentOrganization org, ICurrentUser user, OrgCalendar calendar, IAuditWriter audit, IClock clock, Notifier notifier)
{
    public const int MaxPageSize = 50;
    public const int MaxActiveIssues = 10;

    private Guid? TenantId => org.Role == Domain.Organizations.Role.Tenant ? org.TenantId : null;

    // ---------- account ----------

    public async Task<Result<PortalSummaryDto>> SummaryAsync(CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tid).Select(t => t.FullName).FirstOrDefaultAsync(ct);
        if (tenant is null) return new(Outcome.NotFound);
        var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org.Id).Select(o => o.Currency).FirstAsync(ct);

        var lease = await (from l in db.Leases.AsNoTracking()
                           join u in db.Units on l.UnitId equals u.Id
                           join p in db.Properties on u.PropertyId equals p.Id
                           where l.TenantId == tid && (l.Status == Domain.Leases.LeaseStatus.Active || l.Status == Domain.Leases.LeaseStatus.Ended)
                                 && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)
                           orderby l.StartDate descending
                           select new PortalLeaseDto(l.Id, u.Code, p.Name, p.Address, l.MonthlyRent, l.DueDay, l.StartDate, l.EndDate)).FirstOrDefaultAsync(ct);

        var pending = (await ChargeRows(tid).Where(x => x.c.Status != ChargeStatus.Voided).ToListAsync(ct))
            .Select(x => ToCharge(x.c, x.allocated, today, grace)).Where(c => c.Balance > 0).ToList();
        var overdue = pending.Where(c => c.Status == ChargeStatus.Overdue).ToList();
        var next = pending.Where(c => c.Status != ChargeStatus.Overdue).OrderBy(c => c.DueDate).FirstOrDefault();

        var credit = await (from p in db.Payments.AsNoTracking()
                            where p.TenantId == tid && p.Status == PaymentStatus.Confirmed
                            select p.Amount - (db.PaymentAllocations.Where(a => a.PaymentId == p.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m))
                           .SumAsync(ct);

        var hasContract = lease is not null && await db.LeaseDocuments.AnyAsync(d => d.LeaseId == lease.Id, ct);
        return new(Outcome.Ok, new PortalSummaryDto(tenant, currency, today, lease, pending.Sum(c => c.Balance), overdue.Sum(c => c.Balance), overdue.Count,
            next, Math.Max(0m, credit), hasContract));
    }

    /// <summary>Charges of this tenant's leases. Voided charges are not shown: for the tenant they never existed.</summary>
    public async Task<Result<Paged<PortalChargeDto>>> ChargesAsync(bool pendingOnly, int page, int pageSize, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        var rows = (await ChargeRows(tid).Where(x => x.c.Status != ChargeStatus.Voided).OrderByDescending(x => x.c.DueDate).ThenBy(x => x.c.Id).ToListAsync(ct))
            .Select(x => ToCharge(x.c, x.allocated, today, grace));
        if (pendingOnly) rows = rows.Where(c => c.Balance > 0);
        var all = rows.ToList();
        return new(Outcome.Ok, new Paged<PortalChargeDto>(all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count, page, pageSize));
    }

    public async Task<Result<Paged<PortalPaymentDto>>> PaymentsAsync(int page, int pageSize, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = db.Payments.AsNoTracking().Where(p => p.TenantId == tid);
        var total = await q.CountAsync(ct);
        var payments = await q.OrderByDescending(p => p.ReceivedOn).ThenByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = payments.Select(p => p.Id).ToList();
        var folios = await db.Receipts.AsNoTracking().Where(r => ids.Contains(r.PaymentId)).ToDictionaryAsync(r => r.PaymentId, r => r.Folio, ct);
        var allocs = await (from a in db.PaymentAllocations.AsNoTracking()
                            join c in db.RentCharges on a.RentChargeId equals c.Id
                            where ids.Contains(a.PaymentId) && a.ReversedAt == null
                            orderby c.DueDate
                            select new { a.PaymentId, c.Description, a.Amount }).ToListAsync(ct);
        var items = payments.Select(p =>
        {
            var mine = allocs.Where(a => a.PaymentId == p.Id).Select(a => new PortalAllocationDto(a.Description, a.Amount)).ToList();
            var unassigned = p.Status == PaymentStatus.Confirmed ? Payment.Unassigned(p.Amount, mine.Sum(a => a.Amount)) : 0m;
            return new PortalPaymentDto(p.Id, p.ReceivedOn, p.Amount, p.Method, p.Reference, p.Status, unassigned, p.Status == PaymentStatus.Confirmed ? mine : [],
                folios.TryGetValue(p.Id, out var folio) ? Domain.Documents.Receipt.Format(folio) : null);
        }).ToList();
        return new(Outcome.Ok, new Paged<PortalPaymentDto>(items, total, page, pageSize));
    }

    // ---------- issues ----------

    public async Task<Result<Paged<PortalIssueDto>>> IssuesAsync(int page, int pageSize, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = from i in db.MaintenanceIssues.AsNoTracking()
                join u in db.Units on i.UnitId equals u.Id into units
                from u in units.DefaultIfEmpty()
                where i.ReportedByTenantId == tid
                select new { i, UnitCode = u != null ? u.Code : null };
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.i.ReportedAt).ThenBy(x => x.i.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(Outcome.Ok, new Paged<PortalIssueDto>(rows.Select(r => ToIssue(r.i, r.UnitCode, null)).ToList(), total, page, pageSize));
    }

    public async Task<Result<PortalIssueDto>> GetIssueAsync(Guid id, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        var row = await (from i in db.MaintenanceIssues.AsNoTracking()
                         join u in db.Units on i.UnitId equals u.Id into units
                         from u in units.DefaultIfEmpty()
                         where i.Id == id && i.ReportedByTenantId == tid // another tenant's issue is indistinguishable from a missing one
                         select new { i, UnitCode = u != null ? u.Code : null }).FirstOrDefaultAsync(ct);
        if (row is null) return new(Outcome.NotFound);
        // Only what was written for the tenant: the report itself, status changes (without the staff's note), and comments marked visible.
        var events = await db.MaintenanceIssueEvents.AsNoTracking()
            .Where(e => e.IssueId == id && (e.Type == IssueEventType.Created || e.Type == IssueEventType.StatusChanged || (e.Type == IssueEventType.Comment && e.TenantVisible)))
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct);
        var dtoEvents = events.Select(e => new PortalIssueEventDto(e.Type, e.ActorUserId == row.i.CreatedByUserId && e.Type != IssueEventType.StatusChanged,
            e.ToStatus, e.Type == IssueEventType.StatusChanged ? null : e.Body, e.CreatedAt)).ToList();
        return new(Outcome.Ok, ToIssue(row.i, row.UnitCode, dtoEvents));
    }

    public async Task<Result<PortalIssueDto>> ReportIssueAsync(ReportIssueRequest req, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        var description = req.Description?.Trim() ?? "";
        if (MaintenanceIssue.ValidateText(req.Title ?? "", description) is { } error) return new(Outcome.Invalid, Error: error);

        var today = await calendar.TodayAsync(ct);
        var place = await (from l in db.Leases.AsNoTracking()
                           join u in db.Units on l.UnitId equals u.Id
                           where l.TenantId == tid && (l.Status == Domain.Leases.LeaseStatus.Active || l.Status == Domain.Leases.LeaseStatus.Ended)
                                 && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)
                           orderby l.StartDate descending
                           select new { u.Id, u.PropertyId }).FirstOrDefaultAsync(ct);
        if (place is null) return new(Outcome.Invalid, Error: "No tienes un contrato vigente. Contacta a la administración para reportar el problema.");

        var open = await db.MaintenanceIssues.CountAsync(i => i.ReportedByTenantId == tid
            && (i.Status == IssueStatus.Open || i.Status == IssueStatus.InProgress || i.Status == IssueStatus.Waiting), ct);
        if (open >= MaxActiveIssues) return new(Outcome.Conflict, Error: $"Ya tienes {MaxActiveIssues} reportes abiertos. Espera a que se atiendan algunos.");

        var now = clock.UtcNow;
        var issue = new MaintenanceIssue
        {
            PropertyId = place.PropertyId, UnitId = place.Id, Title = req.Title!.Trim(), Description = description,
            Priority = IssuePriority.Normal, // the tenant does not rank urgency; the administration does
            ReportedAt = now, UpdatedAt = now, CreatedByUserId = user.UserId!.Value, ReportedByTenantId = tid,
        };
        db.MaintenanceIssues.Add(issue);
        db.MaintenanceIssueEvents.Add(new MaintenanceIssueEvent { IssueId = issue.Id, Type = IssueEventType.Created, ActorUserId = issue.CreatedByUserId, TenantVisible = true, CreatedAt = now });
        audit.Write("issue.created", nameof(MaintenanceIssue), issue.Id, metadata: new { issue.PropertyId, issue.UnitId, viaPortal = true });
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tid).Select(t => t.FullName).FirstAsync(ct);
        var unitCode = await db.Units.AsNoTracking().Where(u => u.Id == place.Id).Select(u => u.Code).FirstOrDefaultAsync(ct);
        await notifier.ToStaffAsync(Audience.Managers, NotificationText.IssueReported(tenantName, issue.Title, unitCode, issue.Id), ct);
        await db.SaveChangesAsync(ct);
        return await GetIssueAsync(issue.Id, ct);
    }

    public async Task<Result<PortalIssueDto>> CommentAsync(Guid id, PortalCommentRequest req, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        if (MaintenanceIssueEvent.ValidateComment(req.Body) is { } error) return new(Outcome.Invalid, Error: error);
        var issue = await db.MaintenanceIssues.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.ReportedByTenantId == tid, ct);
        if (issue is null) return new(Outcome.NotFound);
        if (issue.Status == IssueStatus.Closed) return new(Outcome.Conflict, Error: "Este reporte está cerrado. Crea uno nuevo si el problema sigue.");
        db.MaintenanceIssueEvents.Add(new MaintenanceIssueEvent
        {
            IssueId = id, Type = IssueEventType.Comment, ActorUserId = user.UserId!.Value, Body = req.Body!.Trim(), TenantVisible = true, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return await GetIssueAsync(id, ct);
    }

    // ---------- helpers ----------

    private IQueryable<ChargeRow> ChargeRows(Guid tenantId) =>
        from c in db.RentCharges.AsNoTracking()
        join l in db.Leases on c.LeaseId equals l.Id
        where l.TenantId == tenantId
        select new ChargeRow { c = c, allocated = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m };

    private static PortalChargeDto ToCharge(RentCharge c, decimal allocated, DateOnly today, int grace) =>
        new(c.Id, c.Description, c.Type, c.PeriodYear, c.PeriodMonth, c.Amount, Math.Min(allocated, c.Amount), RentCharge.Balance(c.Amount, allocated, voided: false), c.DueDate, RentCharge.Effective(c.Status, c.DueDate, today, grace));

    private static PortalIssueDto ToIssue(MaintenanceIssue i, string? unitCode, IReadOnlyList<PortalIssueEventDto>? events) =>
        new(i.Id, i.Title, i.Description, i.Status, unitCode, i.ReportedAt, i.UpdatedAt, i.ResolvedAt, events);

    private class ChargeRow { public RentCharge c { get; set; } = null!; public decimal allocated { get; set; } }
}
