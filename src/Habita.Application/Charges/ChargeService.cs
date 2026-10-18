using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Domain.Charges;
using Habita.Domain.Common;
using Habita.Domain.Leases;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Charges;

public record ChargeDto(
    Guid Id, Guid LeaseId, Guid UnitId, string UnitCode, Guid PropertyId, string PropertyName, Guid TenantId, string TenantName,
    ChargeType Type, int? PeriodYear, int? PeriodMonth, string Description, decimal Amount, decimal PaidAmount, decimal Balance,
    DateOnly DueDate, ChargeStatus Status, string? VoidReason, DateTimeOffset CreatedAt);

public record CreateChargeRequest(Guid LeaseId, string Description, decimal Amount, DateOnly DueDate, int? PeriodYear, int? PeriodMonth);
public record VoidChargeRequest(string Reason);
public record GenerateChargesRequest(int Year, int Month);

public enum PreviewAction { Create, AlreadyExists, SkippedZeroRent }
public record PreviewItem(Guid LeaseId, string UnitCode, string PropertyName, string TenantName, decimal Amount, DateOnly DueDate, PreviewAction Action);
public record GenerationPreview(int Year, int Month, IReadOnlyList<PreviewItem> Items, int ToCreate, decimal ToCreateTotal, int AlreadyExist, int Skipped);
public record GenerationResult(int Year, int Month, int Created, int AlreadyExisted, int Skipped, decimal CreatedTotal);

public class ChargeService(IAppDbContext db, IAuditWriter audit, IClock clock, OrgCalendar calendar, IChargeStore store, IDbTransactions tx)
{
    public const int MaxPageSize = 100;

    // ---------- queries ----------

    public async Task<Paged<ChargeDto>> ListAsync(ChargeStatus? status, Guid? leaseId, Guid? tenantId, int? year, int? month, string? search,
        bool openOnly, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        var cutoff = RentCharge.OverdueBefore(today, grace); // due before this date = overdue
        var q = Joined();
        if (leaseId is { } lid) q = q.Where(x => x.c.LeaseId == lid);
        if (tenantId is { } tid) q = q.Where(x => x.l.TenantId == tid);
        if (year is { } y) q = q.Where(x => x.c.PeriodYear == y);
        if (month is { } m) q = q.Where(x => x.c.PeriodMonth == m);
        // Charges with a balance to collect (not paid, not voided), oldest due date first: what a payment is applied to.
        if (openOnly) q = q.Where(x => x.c.Status != ChargeStatus.Paid && x.c.Status != ChargeStatus.Voided);
        if (status is { } s)
        {
            q = s switch
            {
                ChargeStatus.Overdue => q.Where(x => (x.c.Status == ChargeStatus.Open || x.c.Status == ChargeStatus.PartiallyPaid) && x.c.DueDate < cutoff),
                ChargeStatus.Open or ChargeStatus.PartiallyPaid => q.Where(x => x.c.Status == s && x.c.DueDate >= cutoff),
                _ => q.Where(x => x.c.Status == s),
            };
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(x => x.t.FullName.ToLower().Contains(term) || x.u.Code.ToLower().Contains(term)
                             || x.p.Name.ToLower().Contains(term) || x.c.Description.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var ordered = openOnly ? q.OrderBy(x => x.c.DueDate).ThenBy(x => x.c.Id) : q.OrderByDescending(x => x.c.DueDate).ThenBy(x => x.t.FullName).ThenBy(x => x.c.Id);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(x => ToDto(x.c, x.u, x.p, x.t, x.allocated, today, grace)).ToList(), total, page, pageSize);
    }

    public async Task<Result<ChargeDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        var x = await Joined().Where(r => r.c.Id == id).FirstOrDefaultAsync(ct);
        return x is null ? new(Outcome.NotFound) : new(Outcome.Ok, ToDto(x.c, x.u, x.p, x.t, x.allocated, today, grace));
    }

    // ---------- manual charges ----------

    public async Task<Result<ChargeDto>> CreateAsync(CreateChargeRequest req, CancellationToken ct)
    {
        if (RentCharge.ValidateManual(req.Description, req.Amount, req.PeriodYear, req.PeriodMonth) is { } error) return new(Outcome.Invalid, Error: error);
        var lease = await db.Leases.Where(l => l.Id == req.LeaseId).Select(l => new { l.Status }).FirstOrDefaultAsync(ct);
        if (lease is null) return new(Outcome.Invalid, Error: "El contrato no existe.");
        if (lease.Status is not (LeaseStatus.Active or LeaseStatus.Ended))
            return new(Outcome.Conflict, Error: "Solo un contrato vigente o terminado admite cargos.");

        var now = clock.UtcNow;
        var c = new RentCharge
        {
            LeaseId = req.LeaseId, Type = ChargeType.Other, PeriodYear = req.PeriodYear, PeriodMonth = req.PeriodMonth,
            Description = req.Description.Trim(), Amount = req.Amount, DueDate = req.DueDate, CreatedAt = now, UpdatedAt = now,
        };
        db.RentCharges.Add(c);
        audit.Write("charge.created", nameof(RentCharge), c.Id, metadata: new { c.LeaseId, c.Type, c.Amount, c.DueDate, c.Description });
        await db.SaveChangesAsync(ct);
        return await GetAsync(c.Id, ct);
    }

    public async Task<Result<ChargeDto>> VoidAsync(Guid id, VoidChargeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) return new(Outcome.Invalid, Error: "El motivo de la anulación es obligatorio.");
        if (req.Reason.Trim().Length > 1000) return new(Outcome.Invalid, Error: "El motivo no puede superar 1000 caracteres.");
        var c = await db.RentCharges.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return new(Outcome.NotFound);
        if (await db.PaymentAllocations.AnyAsync(a => a.RentChargeId == id && a.ReversedAt == null, ct))
            return new(Outcome.Conflict, Error: "El cargo tiene pagos aplicados. Revierte o reasigna esas aplicaciones antes de anularlo.");
        try { c.Void(req.Reason, clock.UtcNow); }
        catch (DomainException e) { return new(Outcome.Conflict, Error: e.Message); }
        c.UpdatedAt = clock.UtcNow;
        audit.Write("charge.voided", nameof(RentCharge), c.Id, c.VoidReason, new { c.Amount, c.Type, c.PeriodYear, c.PeriodMonth });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- monthly generation ----------

    public async Task<Result<GenerationPreview>> PreviewAsync(GenerateChargesRequest req, CancellationToken ct)
    {
        if (RentCharge.ValidatePeriod(req.Year, req.Month) is { } error) return new(Outcome.Invalid, Error: error);
        var items = await BuildPlanAsync(req.Year, req.Month, ct);
        var create = items.Where(i => i.Action == PreviewAction.Create).ToList();
        return new(Outcome.Ok, new GenerationPreview(req.Year, req.Month, items, create.Count, create.Sum(i => i.Amount),
            items.Count(i => i.Action == PreviewAction.AlreadyExists), items.Count(i => i.Action == PreviewAction.SkippedZeroRent)));
    }

    public async Task<Result<GenerationResult>> GenerateAsync(GenerateChargesRequest req, CancellationToken ct)
    {
        if (RentCharge.ValidatePeriod(req.Year, req.Month) is { } error) return new(Outcome.Invalid, Error: error);
        var result = await tx.RunAsync(async () =>
        {
            var plan = await BuildPlanAsync(req.Year, req.Month, ct);
            var now = clock.UtcNow;
            var toCreate = plan.Where(i => i.Action == PreviewAction.Create).Select(i => new RentCharge
            {
                LeaseId = i.LeaseId, Type = ChargeType.Rent, PeriodYear = req.Year, PeriodMonth = req.Month,
                Description = RentCharge.RentDescription(req.Year, req.Month), Amount = i.Amount, DueDate = i.DueDate, CreatedAt = now, UpdatedAt = now,
            }).ToList();

            // The unique index decides: if a concurrent run created some of these first, they are skipped, never duplicated.
            var inserted = toCreate.Count == 0 ? [] : await store.InsertRentChargesIgnoringDuplicatesAsync(toCreate, ct);
            var insertedSet = inserted.ToHashSet();
            var createdTotal = toCreate.Where(c => insertedSet.Contains(c.Id)).Sum(c => c.Amount);
            if (inserted.Count > 0)
            {
                audit.Write("charges.generated", "ChargeBatch", Guid.NewGuid(),
                    metadata: new { req.Year, req.Month, created = inserted.Count, total = createdTotal, chargeIds = inserted.Take(500) });
                await db.SaveChangesAsync(ct);
            }
            return new GenerationResult(req.Year, req.Month, inserted.Count,
                plan.Count(i => i.Action == PreviewAction.AlreadyExists) + (toCreate.Count - inserted.Count),
                plan.Count(i => i.Action == PreviewAction.SkippedZeroRent), createdTotal);
        }, ct);
        return new(Outcome.Ok, result);
    }

    /// <summary>
    /// Leases in force that cover any day of the month. The full monthly rent is charged: no proration on entry or exit month.
    /// A lease terminated earlier has its end date shortened, so months after the termination are excluded.
    /// </summary>
    private async Task<List<PreviewItem>> BuildPlanAsync(int year, int month, CancellationToken ct)
    {
        var (first, last) = RentCharge.PeriodRange(year, month);
        var leases = await (from l in db.Leases
                            join u in db.Units on l.UnitId equals u.Id
                            join p in db.Properties on u.PropertyId equals p.Id
                            join t in db.Tenants on l.TenantId equals t.Id
                            where (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended)
                                  && l.StartDate <= last && (l.EndDate == null || l.EndDate >= first)
                            orderby p.Name, u.Code
                            select new { l.Id, l.MonthlyRent, l.DueDay, UnitCode = u.Code, PropertyName = p.Name, TenantName = t.FullName }).ToListAsync(ct);
        var ids = leases.Select(l => l.Id).ToList();
        var existing = (await db.RentCharges.Where(c => ids.Contains(c.LeaseId) && c.Type == ChargeType.Rent && c.Status != ChargeStatus.Voided
                                                          && c.PeriodYear == year && c.PeriodMonth == month).Select(c => c.LeaseId).ToListAsync(ct)).ToHashSet();
        return leases.Select(l => new PreviewItem(l.Id, l.UnitCode, l.PropertyName, l.TenantName, l.MonthlyRent, RentCharge.DueDateFor(year, month, l.DueDay),
            existing.Contains(l.Id) ? PreviewAction.AlreadyExists : l.MonthlyRent <= 0 ? PreviewAction.SkippedZeroRent : PreviewAction.Create)).ToList();
    }

    // ---------- helpers ----------

    private IQueryable<ChargeRow> Joined() =>
        from c in db.RentCharges
        join l in db.Leases on c.LeaseId equals l.Id
        join u in db.Units on l.UnitId equals u.Id
        join p in db.Properties on u.PropertyId equals p.Id
        join t in db.Tenants on l.TenantId equals t.Id
        select new ChargeRow
        {
            c = c, l = l, u = u, p = p, t = t,
            allocated = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m,
        };

    private static ChargeDto ToDto(RentCharge c, Domain.Properties.Unit u, Domain.Properties.Property p, Domain.Tenants.Tenant t, decimal allocated, DateOnly today, int grace) =>
        new(c.Id, c.LeaseId, u.Id, u.Code, p.Id, p.Name, t.Id, t.FullName, c.Type, c.PeriodYear, c.PeriodMonth, c.Description, c.Amount, allocated,
            RentCharge.Balance(c.Amount, allocated, c.Status == ChargeStatus.Voided), c.DueDate, RentCharge.Effective(c.Status, c.DueDate, today, grace), c.VoidReason, c.CreatedAt);

    private class ChargeRow
    {
        public RentCharge c { get; set; } = null!;
        public Lease l { get; set; } = null!;
        public Domain.Properties.Unit u { get; set; } = null!;
        public Domain.Properties.Property p { get; set; } = null!;
        public Domain.Tenants.Tenant t { get; set; } = null!;
        public decimal allocated { get; set; }
    }
}
