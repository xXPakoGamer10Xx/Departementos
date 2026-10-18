using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Domain.Charges;
using Habita.Domain.Collections;
using Habita.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Collections;

public record PromiseDto(Guid Id, Guid TenantId, string TenantName, Guid ChargeId, string ChargeDescription, decimal ChargeAmount, decimal ChargeBalance, DateOnly DueDate,
    DateOnly PromisedOn, string? Note, PromiseStatus Status, DateTimeOffset CreatedAt, string? CancelReason);
public record CreatePromiseRequest(Guid ChargeId, DateOnly PromisedOn, string? Note);
public record CancelPromiseRequest(string? Reason);
/// <summary>A promise with who recorded it, for the daily reminders (never sent to a client).</summary>
public record ReminderPromise(Guid CreatedByUserId, PromiseDto Dto);

public class PaymentPromiseService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, IAuditWriter audit, IClock clock, OrgCalendar calendar, ICurrentUser user)
{
    public const int MaxPageSize = 100;
    private const int ScanLimit = 1000; // promises are few; statuses are derived, so they are computed over this window and then paged

    public async Task<Paged<PromiseDto>> ListAsync(Guid? tenantId, Guid? chargeId, PromiseStatus? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = BaseQuery();
        if (tenantId is { } t) q = q.Where(x => x.p.TenantId == t);
        if (chargeId is { } c) q = q.Where(x => x.p.ChargeId == c);
        var rows = await q.OrderByDescending(x => x.p.PromisedOn).ThenByDescending(x => x.p.CreatedAt).ThenBy(x => x.p.Id).Take(ScanLimit).ToListAsync(ct);
        var dtos = await ToDtosAsync(rows.Select(r => (r.p, r.TenantName, r.c)).ToList(), ct);
        if (status is { } s) dtos = dtos.Where(d => d.Status == s).ToList();
        return new(dtos.Skip((page - 1) * pageSize).Take(pageSize).ToList(), dtos.Count, page, pageSize);
    }

    /// <summary>Open promises due on <paramref name="today"/> or the day before, with their derived status. The input of the promise reminders.</summary>
    public async Task<List<ReminderPromise>> OpenAroundAsync(DateOnly today, CancellationToken ct)
    {
        var yesterday = today.AddDays(-1);
        var rows = await BaseQuery().Where(x => x.p.CancelledAt == null && (x.p.PromisedOn == today || x.p.PromisedOn == yesterday)).ToListAsync(ct);
        var dtos = await ToDtosAsync(rows.Select(r => (r.p, r.TenantName, r.c)).ToList(), ct);
        return rows.Select((r, i) => new ReminderPromise(r.p.CreatedByUserId, dtos[i])).ToList();
    }

    public async Task<Result<PromiseDto>> CreateAsync(CreatePromiseRequest req, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        if (PaymentPromise.Validate(req.PromisedOn, today, note) is { } error) return new(Outcome.Invalid, Error: error);

        return await tx.RunAsync<Result<PromiseDto>>(async () =>
        {
            await locks.LockScopeAsync($"promise:{req.ChargeId}", ct); // one open promise per charge, even with two people promising at once
            var charge = await (from c in db.RentCharges.AsNoTracking() join l in db.Leases on c.LeaseId equals l.Id where c.Id == req.ChargeId select new { c.Status, c.Amount, l.TenantId }).FirstOrDefaultAsync(ct);
            if (charge is null) return new(Outcome.NotFound);
            if (charge.Status == ChargeStatus.Voided) return new(Outcome.Conflict, Error: "El cargo está anulado.");
            var allocated = await db.PaymentAllocations.AsNoTracking().Where(a => a.RentChargeId == req.ChargeId && a.ReversedAt == null).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
            if (RentCharge.Balance(charge.Amount, allocated, voided: false) <= 0) return new(Outcome.Conflict, Error: "El cargo ya está pagado.");

            var now = clock.UtcNow;
            foreach (var old in await db.PaymentPromises.Where(p => p.ChargeId == req.ChargeId && p.CancelledAt == null).ToListAsync(ct))
            {
                old.Cancel("Reemplazada por una promesa nueva.", now);
                audit.Write("promise.cancelled", nameof(PaymentPromise), old.Id, reason: old.CancelReason);
            }
            await db.SaveChangesAsync(ct); // the old one leaves first: the unique index allows a single open promise per charge

            var promise = new PaymentPromise
            {
                TenantId = charge.TenantId, ChargeId = req.ChargeId, PromisedOn = req.PromisedOn, Note = note, CreatedByUserId = user.UserId!.Value, CreatedAt = now,
            };
            db.PaymentPromises.Add(promise);
            audit.Write("promise.created", nameof(PaymentPromise), promise.Id, metadata: new { promise.ChargeId, promise.TenantId, promise.PromisedOn });
            await db.SaveChangesAsync(ct);
            return new(Outcome.Ok, await LoadAsync(promise.Id, ct));
        }, ct);
    }

    public async Task<Result<PromiseDto>> CancelAsync(Guid id, CancelPromiseRequest req, CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(req.Reason) ? "Cancelada por el personal." : req.Reason.Trim();
        if (reason.Length > 500) return new(Outcome.Invalid, Error: "El motivo no puede superar 500 caracteres.");
        var promise = await db.PaymentPromises.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (promise is null) return new(Outcome.NotFound);
        if (promise.CancelledAt is not null) return new(Outcome.Conflict, Error: "La promesa ya está cancelada.");
        promise.Cancel(reason, clock.UtcNow);
        audit.Write("promise.cancelled", nameof(PaymentPromise), promise.Id, reason);
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, await LoadAsync(id, ct));
    }

    private IQueryable<PromiseRow> BaseQuery() =>
        from p in db.PaymentPromises.AsNoTracking()
        join t in db.Tenants on p.TenantId equals t.Id
        join c in db.RentCharges on p.ChargeId equals c.Id
        select new PromiseRow { p = p, TenantName = t.FullName, c = c };

    private async Task<PromiseDto> LoadAsync(Guid id, CancellationToken ct)
    {
        var row = await BaseQuery().Where(x => x.p.Id == id).FirstAsync(ct);
        return (await ToDtosAsync([(row.p, row.TenantName, row.c)], ct))[0];
    }

    private async Task<List<PromiseDto>> ToDtosAsync(List<(PaymentPromise p, string TenantName, RentCharge c)> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var today = await calendar.TodayAsync(ct);
        var chargeIds = rows.Select(r => r.c.Id).Distinct().ToList();
        var received = await (from a in db.PaymentAllocations.AsNoTracking()
                              join pay in db.Payments on a.PaymentId equals pay.Id
                              where chargeIds.Contains(a.RentChargeId) && a.ReversedAt == null && pay.Status == PaymentStatus.Confirmed
                              select new { a.RentChargeId, a.Amount, pay.ReceivedOn }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var mine = received.Where(x => x.RentChargeId == r.c.Id).ToList();
            var onTime = mine.Where(x => x.ReceivedOn <= r.p.PromisedOn).Sum(x => x.Amount);
            var status = PaymentPromise.StatusFor(r.p.PromisedOn, today, r.c.Amount, onTime, r.c.Status == ChargeStatus.Voided, r.p.CancelledAt is not null);
            return new PromiseDto(r.p.Id, r.p.TenantId, r.TenantName, r.c.Id, r.c.Description, r.c.Amount,
                RentCharge.Balance(r.c.Amount, mine.Sum(x => x.Amount), r.c.Status == ChargeStatus.Voided), r.c.DueDate, r.p.PromisedOn, r.p.Note, status, r.p.CreatedAt, r.p.CancelReason);
        }).ToList();
    }

    private class PromiseRow { public PaymentPromise p { get; set; } = null!; public string TenantName { get; set; } = ""; public RentCharge c { get; set; } = null!; }
}
