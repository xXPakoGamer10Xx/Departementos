using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Domain.Charges;
using Habita.Domain.Common;
using Habita.Domain.Documents;
using Habita.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Payments;

public record AllocationInput(Guid ChargeId, decimal Amount);
public record CreatePaymentRequest(Guid TenantId, DateOnly ReceivedOn, decimal Amount, PaymentMethod Method, string? Reference, string? Notes, List<AllocationInput>? Allocations);
public record AllocatePaymentRequest(List<AllocationInput>? Allocations);
public record ReasonRequest(string Reason);

public record AllocationDto(Guid Id, Guid ChargeId, string ChargeDescription, string UnitCode, string PropertyName, decimal Amount,
    DateTimeOffset CreatedAt, DateTimeOffset? ReversedAt, string? ReversalReason);

public record PaymentDto(Guid Id, Guid TenantId, string TenantName, DateOnly ReceivedOn, decimal Amount, PaymentMethod Method, string? Reference, string? Notes,
    PaymentStatus Status, decimal AllocatedAmount, decimal UnassignedAmount, DateTimeOffset CreatedAt, DateTimeOffset? ReversedAt, string? ReversalReason,
    IReadOnlyList<AllocationDto>? Allocations, string? ReceiptFolio = null);

public record PaymentCreated(PaymentDto Payment, bool Replayed);

public class PaymentService(IAppDbContext db, IAuditWriter audit, IClock clock, OrgCalendar calendar, ICurrentUser user, IDbTransactions tx, IFinanceLocks locks, IReceiptFolios folios)
{
    public const int MaxPageSize = 100;
    private static readonly CultureInfo Mx = new("es-MX");

    // ---------- queries ----------

    public async Task<Paged<PaymentDto>> ListAsync(Guid? tenantId, PaymentStatus? status, DateOnly? from, DateOnly? to, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = from p in db.Payments join t in db.Tenants on p.TenantId equals t.Id select new { p, t.FullName };
        if (tenantId is { } tid) q = q.Where(x => x.p.TenantId == tid);
        if (status is { } s) q = q.Where(x => x.p.Status == s);
        if (from is { } f) q = q.Where(x => x.p.ReceivedOn >= f);
        if (to is { } e) q = q.Where(x => x.p.ReceivedOn <= e);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(x => x.FullName.ToLower().Contains(term) || (x.p.Reference != null && x.p.Reference.ToLower().Contains(term)));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.p.ReceivedOn).ThenByDescending(x => x.p.CreatedAt).ThenBy(x => x.p.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Select(r => r.p.Id).ToList();
        var receipts = await db.Receipts.AsNoTracking().Where(r => ids.Contains(r.PaymentId)).ToDictionaryAsync(r => r.PaymentId, r => r.Folio, ct);
        var allocated = await db.PaymentAllocations.AsNoTracking().Where(a => ids.Contains(a.PaymentId) && a.ReversedAt == null)
            .GroupBy(a => a.PaymentId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return new(rows.Select(r => ToDto(r.p, r.FullName, allocated.GetValueOrDefault(r.p.Id), null, receipts.TryGetValue(r.p.Id, out var folio) ? Receipt.Format(folio) : null)).ToList(), total, page, pageSize);
    }

    public async Task<Result<PaymentDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var dto = await LoadAsync(id, ct);
        return dto is null ? new(Outcome.NotFound) : new(Outcome.Ok, dto);
    }

    // ---------- register a payment (payment + allocations + audit in one transaction) ----------

    public async Task<Result<PaymentCreated>> CreateAsync(CreatePaymentRequest req, string? idempotencyKey, CancellationToken ct)
    {
        var allocs = req.Allocations ?? [];
        if (idempotencyKey is not null && idempotencyKey.Trim().Length is 0 or > 100) return new(Outcome.Invalid, Error: "La clave de idempotencia debe tener entre 1 y 100 caracteres.");
        idempotencyKey = idempotencyKey?.Trim();
        if (!Enum.IsDefined(req.Method)) return new(Outcome.Invalid, Error: "El método de pago no es válido.");
        var reference = Blank(req.Reference); var notes = Blank(req.Notes);
        var today = await calendar.TodayAsync(ct);
        if (Payment.ValidateNew(req.Amount, req.ReceivedOn, today, reference, notes) is { } error) return new(Outcome.Invalid, Error: error);
        var hash = Fingerprint(req, allocs, reference, notes);

        return await tx.RunAsync<Result<PaymentCreated>>(async () =>
        {
            if (idempotencyKey is not null)
            {
                // Serializes identical keys: a concurrent duplicate waits here, then finds the first payment and replays it.
                await locks.LockIdempotencyKeyAsync(idempotencyKey, ct);
                var existing = await db.Payments.AsNoTracking().Where(p => p.IdempotencyKey == idempotencyKey).Select(p => new { p.Id, p.RequestHash }).FirstOrDefaultAsync(ct);
                if (existing is not null)
                    return existing.RequestHash == hash
                        ? new(Outcome.Ok, new PaymentCreated((await LoadAsync(existing.Id, ct))!, Replayed: true))
                        : new(Outcome.Conflict, Error: "Esa clave de idempotencia ya se usó con una solicitud distinta.");
            }

            var tenantName = await db.Tenants.Where(t => t.Id == req.TenantId).Select(t => t.FullName).FirstOrDefaultAsync(ct);
            if (tenantName is null) return new(Outcome.Invalid, Error: "El inquilino no existe.");

            if (await ValidateInputsAsync(req.TenantId, allocs, req.Amount, ct) is { } invalid) return new(invalid.Outcome, Error: invalid.Error);

            var chargeIds = allocs.Select(a => a.ChargeId).Distinct().OrderBy(id => id).ToList();
            await locks.LockChargesAsync(chargeIds, ct);
            var (charges, allocated) = await LoadChargesAsync(chargeIds, ct);
            if (CheckBalances(allocs, charges, allocated) is { } overBalance) return new(Outcome.Conflict, Error: overBalance);

            var now = clock.UtcNow;
            var payment = new Payment
            {
                TenantId = req.TenantId, ReceivedOn = req.ReceivedOn, Amount = req.Amount, Method = req.Method, Reference = reference, Notes = notes,
                CreatedByUserId = user.UserId!.Value, IdempotencyKey = idempotencyKey, RequestHash = hash, CreatedAt = now,
            };
            db.Payments.Add(payment);
            // The receipt number is taken last, inside this transaction: if the payment fails to save, the number goes back too, so numbers have no gaps.
            db.Receipts.Add(new Receipt { PaymentId = payment.Id, Folio = await folios.NextAsync(ct), IssuedAt = now });
            foreach (var a in allocs.OrderBy(a => a.ChargeId))
                db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = payment.Id, RentChargeId = a.ChargeId, Amount = a.Amount, CreatedAt = now });
            audit.Write("payment.created", nameof(Payment), payment.Id,
                metadata: new { payment.TenantId, payment.Amount, payment.Method, payment.ReceivedOn, allocations = allocs.Select(a => new { a.ChargeId, a.Amount }) });
            await db.SaveChangesAsync(ct);
            await RecomputeStatusesAsync(charges, ct);
            return new(Outcome.Ok, new PaymentCreated((await LoadAsync(payment.Id, ct))!, Replayed: false));
        }, ct);
    }

    // ---------- apply the unassigned balance explicitly ----------

    public async Task<Result<PaymentDto>> AllocateAsync(Guid paymentId, AllocatePaymentRequest req, CancellationToken ct)
    {
        var allocs = req.Allocations ?? [];
        if (allocs.Count == 0) return new(Outcome.Invalid, Error: "Indica al menos una aplicación.");
        var head = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => new { p.TenantId, p.Status, p.Amount }).FirstOrDefaultAsync(ct);
        if (head is null) return new(Outcome.NotFound);
        if (head.Status != PaymentStatus.Confirmed) return new(Outcome.Conflict, Error: "Un pago revertido no admite aplicaciones.");
        if (await ValidateInputsAsync(head.TenantId, allocs, head.Amount, ct) is { } invalid) return new(invalid.Outcome, Error: invalid.Error);

        return await tx.RunAsync<Result<PaymentDto>>(async () =>
        {
            await locks.LockPaymentAsync(paymentId, ct);
            var chargeIds = allocs.Select(a => a.ChargeId).Distinct().OrderBy(id => id).ToList();
            await locks.LockChargesAsync(chargeIds, ct);

            // Everything below is read after the locks, so concurrent requests see each other's committed result.
            var payment = await db.Payments.FirstAsync(p => p.Id == paymentId, ct);
            if (payment.Status != PaymentStatus.Confirmed) return new(Outcome.Conflict, Error: "Un pago revertido no admite aplicaciones.");
            var paymentAllocated = await db.PaymentAllocations.AsNoTracking().Where(a => a.PaymentId == paymentId && a.ReversedAt == null).SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
            var unassigned = Payment.Unassigned(payment.Amount, paymentAllocated);
            var requested = allocs.Sum(a => a.Amount);
            if (requested > unassigned) return new(Outcome.Conflict, Error: $"El saldo no asignado del pago es {unassigned.ToString("C2", Mx)}; no puedes aplicar {requested.ToString("C2", Mx)}.");

            var (charges, allocated) = await LoadChargesAsync(chargeIds, ct);
            if (CheckBalances(allocs, charges, allocated) is { } overBalance) return new(Outcome.Conflict, Error: overBalance);

            var now = clock.UtcNow;
            foreach (var a in allocs.OrderBy(a => a.ChargeId))
                db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = paymentId, RentChargeId = a.ChargeId, Amount = a.Amount, CreatedAt = now });
            audit.Write("payment.allocated", nameof(Payment), paymentId, metadata: new { allocations = allocs.Select(a => new { a.ChargeId, a.Amount }) });
            await db.SaveChangesAsync(ct);
            await RecomputeStatusesAsync(charges, ct);
            return new(Outcome.Ok, (await LoadAsync(paymentId, ct))!);
        }, ct);
    }

    // ---------- reversals (never edit or delete a confirmed payment) ----------

    public async Task<Result<PaymentDto>> ReverseAsync(Guid paymentId, ReasonRequest req, CancellationToken ct)
    {
        if (ReasonError(req.Reason) is { } error) return new(Outcome.Invalid, Error: error);
        if (!await db.Payments.AnyAsync(p => p.Id == paymentId, ct)) return new(Outcome.NotFound);

        return await tx.RunAsync<Result<PaymentDto>>(async () =>
        {
            await locks.LockPaymentAsync(paymentId, ct);
            var payment = await db.Payments.FirstAsync(p => p.Id == paymentId, ct);
            if (payment.Status == PaymentStatus.Reversed) return new(Outcome.Conflict, Error: "El pago ya está revertido.");

            // The payment lock keeps new allocations out, so this list is stable while we lock the affected charges.
            var active = await db.PaymentAllocations.Where(a => a.PaymentId == paymentId && a.ReversedAt == null).ToListAsync(ct);
            var chargeIds = active.Select(a => a.RentChargeId).Distinct().OrderBy(id => id).ToList();
            await locks.LockChargesAsync(chargeIds, ct);
            var (charges, _) = await LoadChargesAsync(chargeIds, ct);

            var now = clock.UtcNow;
            foreach (var a in active) a.Reverse($"Reverso del pago: {req.Reason.Trim()}", now);
            payment.Reverse(req.Reason, user.UserId!.Value, now);
            audit.Write("payment.reversed", nameof(Payment), paymentId, payment.ReversalReason,
                new { payment.Amount, allocationsReversed = active.Count, chargeIds });
            await db.SaveChangesAsync(ct);
            await RecomputeStatusesAsync(charges, ct);
            return new(Outcome.Ok, (await LoadAsync(paymentId, ct))!);
        }, ct);
    }

    /// <summary>Un-applies one allocation, freeing that amount on the payment so it can be applied elsewhere ("reasignar").</summary>
    public async Task<Result<PaymentDto>> ReverseAllocationAsync(Guid paymentId, Guid allocationId, ReasonRequest req, CancellationToken ct)
    {
        if (ReasonError(req.Reason) is { } error) return new(Outcome.Invalid, Error: error);
        if (!await db.PaymentAllocations.AnyAsync(a => a.Id == allocationId && a.PaymentId == paymentId, ct)) return new(Outcome.NotFound);

        return await tx.RunAsync<Result<PaymentDto>>(async () =>
        {
            await locks.LockPaymentAsync(paymentId, ct);
            var allocation = await db.PaymentAllocations.FirstAsync(a => a.Id == allocationId && a.PaymentId == paymentId, ct);
            if (allocation.ReversedAt is not null) return new(Outcome.Conflict, Error: "La aplicación ya está revertida.");
            await locks.LockChargesAsync([allocation.RentChargeId], ct);
            var (charges, _) = await LoadChargesAsync([allocation.RentChargeId], ct);

            allocation.Reverse(req.Reason, clock.UtcNow);
            audit.Write("allocation.reversed", nameof(Payment), paymentId, allocation.ReversalReason, new { allocationId, allocation.RentChargeId, allocation.Amount });
            await db.SaveChangesAsync(ct);
            await RecomputeStatusesAsync(charges, ct);
            return new(Outcome.Ok, (await LoadAsync(paymentId, ct))!);
        }, ct);
    }

    // ---------- helpers ----------

    private record ChargeFacts(Guid Id, ChargeStatus Status, Guid TenantId);

    /// <summary>Pre-lock validation: shape, same tenant, not voided, and total within the amount available.</summary>
    private async Task<Result<object>?> ValidateInputsAsync(Guid tenantId, List<AllocationInput> allocs, decimal available, CancellationToken ct)
    {
        if (allocs.Count == 0) return null;
        if (allocs.Select(a => a.ChargeId).Distinct().Count() != allocs.Count) return new(Outcome.Invalid, Error: "Un mismo cargo no puede aparecer dos veces en las aplicaciones.");
        foreach (var a in allocs)
            if (PaymentAllocation.ValidateAmount(a.Amount) is { } amountError) return new(Outcome.Invalid, Error: amountError);
        var total = allocs.Sum(a => a.Amount);
        if (total > available) return new(Outcome.Invalid, Error: $"La suma aplicada ({total.ToString("C2", Mx)}) supera el importe disponible del pago ({available.ToString("C2", Mx)}).");

        var ids = allocs.Select(a => a.ChargeId).ToList();
        var facts = await (from c in db.RentCharges join l in db.Leases on c.LeaseId equals l.Id where ids.Contains(c.Id) select new ChargeFacts(c.Id, c.Status, l.TenantId)).ToListAsync(ct);
        if (facts.Count != ids.Count) return new(Outcome.Invalid, Error: "Alguno de los cargos no existe.");
        if (facts.Any(f => f.TenantId != tenantId)) return new(Outcome.Invalid, Error: "Un pago solo puede aplicarse a cargos del mismo inquilino.");
        if (facts.Any(f => f.Status == ChargeStatus.Voided)) return new(Outcome.Conflict, Error: "No se puede aplicar un pago a un cargo anulado.");
        return null;
    }

    /// <summary>Tracked charges plus their active allocation sums, read fresh (call only after taking the locks).</summary>
    private async Task<(List<RentCharge> Charges, Dictionary<Guid, decimal> Allocated)> LoadChargesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var charges = await db.RentCharges.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var allocated = await db.PaymentAllocations.AsNoTracking().Where(a => ids.Contains(a.RentChargeId) && a.ReversedAt == null)
            .GroupBy(a => a.RentChargeId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        return (charges, allocated);
    }

    private static string? CheckBalances(List<AllocationInput> allocs, List<RentCharge> charges, Dictionary<Guid, decimal> allocated)
    {
        foreach (var a in allocs)
        {
            var c = charges.FirstOrDefault(x => x.Id == a.ChargeId);
            if (c is null) return "Alguno de los cargos no existe.";
            if (c.Status == ChargeStatus.Voided) return "No se puede aplicar un pago a un cargo anulado.";
            var balance = RentCharge.Balance(c.Amount, allocated.GetValueOrDefault(c.Id), voided: false);
            if (a.Amount > balance) return $"El cargo «{c.Description}» solo tiene un saldo de {balance.ToString("C2", Mx)}; no puedes aplicarle {a.Amount.ToString("C2", Mx)}.";
        }
        return null;
    }

    /// <summary>After allocation changes are saved, set each charge's stored status from its fresh active total.</summary>
    private async Task RecomputeStatusesAsync(List<RentCharge> charges, CancellationToken ct)
    {
        if (charges.Count == 0) return;
        var ids = charges.Select(c => c.Id).ToList();
        var sums = await db.PaymentAllocations.AsNoTracking().Where(a => ids.Contains(a.RentChargeId) && a.ReversedAt == null)
            .GroupBy(a => a.RentChargeId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var now = clock.UtcNow;
        foreach (var c in charges.Where(c => c.Status != ChargeStatus.Voided))
        {
            var status = RentCharge.StatusFor(c.Amount, sums.GetValueOrDefault(c.Id));
            if (status != c.Status) { c.Status = status; c.UpdatedAt = now; }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<PaymentDto?> LoadAsync(Guid id, CancellationToken ct)
    {
        var row = await (from p in db.Payments.AsNoTracking() join t in db.Tenants on p.TenantId equals t.Id where p.Id == id select new { p, t.FullName }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var allocations = await (from a in db.PaymentAllocations.AsNoTracking()
                                 join c in db.RentCharges on a.RentChargeId equals c.Id
                                 join l in db.Leases on c.LeaseId equals l.Id
                                 join u in db.Units on l.UnitId equals u.Id
                                 join pr in db.Properties on u.PropertyId equals pr.Id
                                 where a.PaymentId == id orderby a.CreatedAt, c.DueDate
                                 select new AllocationDto(a.Id, c.Id, c.Description, u.Code, pr.Name, a.Amount, a.CreatedAt, a.ReversedAt, a.ReversalReason)).ToListAsync(ct);
        var allocated = allocations.Where(a => a.ReversedAt is null).Sum(a => a.Amount);
        var folio = await db.Receipts.AsNoTracking().Where(r => r.PaymentId == id).Select(r => (int?)r.Folio).FirstOrDefaultAsync(ct);
        return ToDto(row.p, row.FullName, allocated, allocations, folio is { } f ? Receipt.Format(f) : null);
    }

    private static PaymentDto ToDto(Payment p, string tenantName, decimal allocated, IReadOnlyList<AllocationDto>? allocations, string? receiptFolio = null) =>
        new(p.Id, p.TenantId, tenantName, p.ReceivedOn, p.Amount, p.Method, p.Reference, p.Notes, p.Status, allocated,
            p.Status == PaymentStatus.Confirmed ? Payment.Unassigned(p.Amount, allocated) : 0m, p.CreatedAt, p.ReversedAt, p.ReversalReason, allocations, receiptFolio);

    private static string? ReasonError(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "El motivo es obligatorio." : reason.Trim().Length > 900 ? "El motivo no puede superar 900 caracteres." : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Identifies "the same request" for idempotent replays: same key + same content replays, same key + different content is rejected.</summary>
    private static string Fingerprint(CreatePaymentRequest r, List<AllocationInput> allocs, string? reference, string? notes)
    {
        var parts = new StringBuilder()
            .Append(r.TenantId).Append('|').Append(r.Amount.ToString(CultureInfo.InvariantCulture)).Append('|').Append(r.ReceivedOn.ToString("O")).Append('|')
            .Append(r.Method).Append('|').Append(reference).Append('|').Append(notes);
        foreach (var a in allocs.OrderBy(a => a.ChargeId)) parts.Append('|').Append(a.ChargeId).Append(':').Append(a.Amount.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToString())));
    }
}
