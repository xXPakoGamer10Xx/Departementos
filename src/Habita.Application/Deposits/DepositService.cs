using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Charges;
using Habita.Domain.Deposits;
using Habita.Domain.Leases;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Deposits;

public record InstallmentInput(DateOnly DueDate, decimal Amount);
public record ScheduleDepositRequest(List<InstallmentInput>? Installments);
public record DeductionInput(string? Concept, decimal Amount);
public record SettleDepositRequest(List<DeductionInput>? Deductions, DateOnly? SettledOn, string? Notes);
public record VoidSettlementRequest(string? Reason);

public record DepositInstallmentDto(Guid ChargeId, string Description, DateOnly DueDate, decimal Amount, decimal Paid, decimal Balance, ChargeStatus Status);
public record DeductionDto(string Concept, decimal Amount);
public record DepositSettlementDto(Guid Id, decimal Held, decimal DeductionsTotal, decimal Refund, DateOnly SettledOn, string? Notes, IReadOnlyList<DeductionDto> Deductions,
    DateTimeOffset CreatedAt, DateTimeOffset? VoidedAt, string? VoidReason);

/// <summary>
/// DepositAmount is what the contract says. Scheduled is what has been turned into installments, Paid what was actually collected and applied, and Custody what is
/// held right now: paid, minus what a live settlement has already closed.
/// </summary>
public record DepositDto(Guid LeaseId, LeaseStatus LeaseStatus, decimal? DepositAmount, decimal Scheduled, decimal Unscheduled, decimal Paid, decimal Outstanding, decimal Custody,
    IReadOnlyList<DepositInstallmentDto> Installments, DepositSettlementDto? Settlement, IReadOnlyList<DepositSettlementDto> VoidedSettlements);

/// <summary>
/// A deposit is collected like any other charge (installments with their own due dates, paid and applied through normal payments) but it is the tenant's money
/// held in custody: it is not income, and it only leaves custody through a settlement, with every deduction written down with its reason.
/// </summary>
public class DepositService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, IAuditWriter audit, IClock clock, OrgCalendar calendar, ICurrentUser user)
{
    public async Task<Result<DepositDto>> GetAsync(Guid leaseId, CancellationToken ct)
    {
        var lease = await db.Leases.AsNoTracking().Where(l => l.Id == leaseId).Select(l => new { l.DepositAmount, l.Status }).FirstOrDefaultAsync(ct);
        if (lease is null) return new(Outcome.NotFound);

        var rows = await (from c in db.RentCharges.AsNoTracking()
                          where c.LeaseId == leaseId && c.Type == ChargeType.Deposit && c.Status != ChargeStatus.Voided
                          orderby c.DueDate, c.CreatedAt, c.Id
                          select new { c, Allocated = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m }).ToListAsync(ct);
        var today = await calendar.TodayAsync(ct); var grace = await calendar.GraceDaysAsync(ct);
        var installments = rows.Select(r => new DepositInstallmentDto(r.c.Id, r.c.Description, r.c.DueDate, r.c.Amount, Math.Min(r.Allocated, r.c.Amount),
            RentCharge.Balance(r.c.Amount, r.Allocated, voided: false), RentCharge.Effective(r.c.Status, r.c.DueDate, today, grace))).ToList();

        var settlements = await db.DepositSettlements.AsNoTracking().Where(s => s.LeaseId == leaseId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
        var ids = settlements.Select(s => s.Id).ToList();
        var deductions = await db.DepositDeductions.AsNoTracking().Where(d => ids.Contains(d.SettlementId)).ToListAsync(ct);
        DepositSettlementDto Dto(DepositSettlement s) => new(s.Id, s.HeldAmount, s.DeductionsTotal, s.RefundAmount, s.SettledOn, s.Notes,
            deductions.Where(d => d.SettlementId == s.Id).OrderBy(d => d.SortOrder).Select(d => new DeductionDto(d.Concept, d.Amount)).ToList(), s.CreatedAt, s.VoidedAt, s.VoidReason);

        var scheduled = installments.Sum(i => i.Amount); var paid = installments.Sum(i => i.Paid);
        var live = settlements.FirstOrDefault(s => s.IsActive);
        return new(Outcome.Ok, new DepositDto(leaseId, lease.Status, lease.DepositAmount, scheduled, Math.Max(0m, (lease.DepositAmount ?? 0m) - scheduled), paid,
            installments.Sum(i => i.Balance), Math.Max(0m, paid - (live?.HeldAmount ?? 0m)), installments, live is null ? null : Dto(live), settlements.Where(s => !s.IsActive).Select(Dto).ToList()));
    }

    public async Task<Result<DepositDto>> ScheduleAsync(Guid leaseId, ScheduleDepositRequest req, CancellationToken ct)
    {
        var inputs = req.Installments ?? [];
        if (inputs.Count is 0 or > DepositRules.MaxInstallments) return new(Outcome.Invalid, Error: $"Indica de 1 a {DepositRules.MaxInstallments} abonos.");
        foreach (var i in inputs) if (DepositRules.ValidateInstallment(i.Amount, i.DueDate) is { } error) return new(Outcome.Invalid, Error: error);

        return await tx.RunAsync<Result<DepositDto>>(async () =>
        {
            await locks.LockScopeAsync($"deposit:{leaseId}", ct); // two people scheduling at once cannot both fill the same remaining amount
            var lease = await db.Leases.AsNoTracking().Where(l => l.Id == leaseId).Select(l => new { l.DepositAmount, l.Status }).FirstOrDefaultAsync(ct);
            if (lease is null) return new(Outcome.NotFound);
            if (lease.Status is not (LeaseStatus.Draft or LeaseStatus.Active)) return new(Outcome.Conflict, Error: "Solo un contrato en borrador o vigente puede programar su depósito.");
            if (lease.DepositAmount is not { } deposit || deposit <= 0) return new(Outcome.Invalid, Error: "El contrato no tiene un depósito. Indícalo primero en las condiciones del contrato.");

            var existing = await db.RentCharges.AsNoTracking().Where(c => c.LeaseId == leaseId && c.Type == ChargeType.Deposit && c.Status != ChargeStatus.Voided).Select(c => c.Amount).ToListAsync(ct);
            var remaining = deposit - existing.Sum();
            var total = inputs.Sum(i => i.Amount);
            if (total != remaining)
                return new(Outcome.Invalid, Error: remaining <= 0 ? "El depósito ya está programado por completo." : $"Los abonos deben sumar exactamente lo que falta programar ({remaining:N2}); sumaron {total:N2}.");

            var now = clock.UtcNow; var count = existing.Count + inputs.Count; var number = existing.Count;
            foreach (var i in inputs.OrderBy(i => i.DueDate))
            {
                number++;
                db.RentCharges.Add(new RentCharge
                {
                    LeaseId = leaseId, Type = ChargeType.Deposit, Description = count == 1 ? "Depósito" : $"Depósito (abono {number} de {count})",
                    Amount = i.Amount, DueDate = i.DueDate, CreatedAt = now, UpdatedAt = now,
                });
            }
            audit.Write("deposit.scheduled", nameof(Lease), leaseId, metadata: new { installments = inputs.Count, total });
            await db.SaveChangesAsync(ct);
            return await GetAsync(leaseId, ct);
        }, ct);
    }

    public async Task<Result<DepositDto>> SettleAsync(Guid leaseId, SettleDepositRequest req, CancellationToken ct)
    {
        var deductions = (req.Deductions ?? []).Select(d => (Concept: d.Concept?.Trim(), d.Amount)).ToList();
        var today = await calendar.TodayAsync(ct);
        var settledOn = req.SettledOn ?? today;
        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();

        return await tx.RunAsync<Result<DepositDto>>(async () =>
        {
            await locks.LockScopeAsync($"deposit:{leaseId}", ct);
            var lease = await db.Leases.AsNoTracking().Where(l => l.Id == leaseId).Select(l => new { l.Status }).FirstOrDefaultAsync(ct);
            if (lease is null) return new(Outcome.NotFound);
            if (lease.Status is not (LeaseStatus.Ended or LeaseStatus.Cancelled)) return new(Outcome.Conflict, Error: "El depósito se liquida cuando el contrato ya terminó o se canceló.");
            if (await db.DepositSettlements.AnyAsync(s => s.LeaseId == leaseId && s.VoidedAt == null, ct))
                return new(Outcome.Conflict, Error: "El depósito ya está liquidado. Si hubo un error, anula la liquidación y hazla de nuevo.");

            // Payments may be applied to the installments while this runs: lock them, then read what was really collected.
            var chargeIds = await db.RentCharges.AsNoTracking().Where(c => c.LeaseId == leaseId && c.Type == ChargeType.Deposit && c.Status != ChargeStatus.Voided).Select(c => c.Id).OrderBy(id => id).ToListAsync(ct);
            await locks.LockChargesAsync(chargeIds, ct);
            var allocated = await db.PaymentAllocations.AsNoTracking().Where(a => chargeIds.Contains(a.RentChargeId) && a.ReversedAt == null)
                .GroupBy(a => a.RentChargeId).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
            var charges = await db.RentCharges.AsNoTracking().Where(c => chargeIds.Contains(c.Id)).ToListAsync(ct);
            if (charges.Any(c => RentCharge.Balance(c.Amount, allocated.GetValueOrDefault(c.Id), voided: false) > 0))
                return new(Outcome.Conflict, Error: "Hay abonos del depósito sin cobrar. Cóbralos o anúlalos antes de liquidar.");
            var held = charges.Sum(c => Math.Min(c.Amount, allocated.GetValueOrDefault(c.Id)));

            if (DepositRules.ValidateSettlement(held, deductions, settledOn, today, notes) is { } error) return new(Outcome.Invalid, Error: error);
            var total = deductions.Sum(d => d.Amount);
            var settlement = new DepositSettlement
            {
                LeaseId = leaseId, HeldAmount = held, DeductionsTotal = total, RefundAmount = held - total, SettledOn = settledOn, Notes = notes,
                SettledByUserId = user.UserId!.Value, CreatedAt = clock.UtcNow,
            };
            db.DepositSettlements.Add(settlement);
            for (var n = 0; n < deductions.Count; n++) db.DepositDeductions.Add(new DepositDeduction { SettlementId = settlement.Id, Concept = deductions[n].Concept!, Amount = deductions[n].Amount, SortOrder = n });
            audit.Write("deposit.settled", nameof(DepositSettlement), settlement.Id, metadata: new { leaseId, held, deductions = total, refund = held - total }); // amounts only, never the reasons
            await db.SaveChangesAsync(ct);
            return await GetAsync(leaseId, ct);
        }, ct);
    }

    public async Task<Result<DepositDto>> VoidSettlementAsync(Guid leaseId, VoidSettlementRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) return new(Outcome.Invalid, Error: "El motivo de la anulación es obligatorio.");
        if (req.Reason.Trim().Length > 1000) return new(Outcome.Invalid, Error: "El motivo no puede superar 1000 caracteres.");
        return await tx.RunAsync<Result<DepositDto>>(async () =>
        {
            await locks.LockScopeAsync($"deposit:{leaseId}", ct);
            var s = await db.DepositSettlements.FirstOrDefaultAsync(x => x.LeaseId == leaseId && x.VoidedAt == null, ct);
            if (s is null) return new(await db.Leases.AnyAsync(l => l.Id == leaseId, ct) ? Outcome.Conflict : Outcome.NotFound, Error: "No hay una liquidación vigente que anular.");
            s.Void(req.Reason, user.UserId!.Value, clock.UtcNow);
            audit.Write("deposit.settlement_voided", nameof(DepositSettlement), s.Id, s.VoidReason, new { leaseId, s.HeldAmount, s.RefundAmount });
            await db.SaveChangesAsync(ct);
            return await GetAsync(leaseId, ct);
        }, ct);
    }
}
