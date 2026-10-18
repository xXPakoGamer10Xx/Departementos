using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Notifications;
using Habita.Application.Payments;
using Habita.Domain.Charges;
using Habita.Domain.Collections;
using Habita.Domain.Payments;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Collections;

public record ResolveCashTokenRequest(string? Code);
public record CashChargeDto(Guid ChargeId, string Description, DateOnly DueDate, decimal Amount, decimal Balance, ChargeStatus Status, string UnitCode, string PropertyName);
public record CashTokenInfo(Guid TokenId, Guid TenantId, string TenantName, DateTimeOffset ExpiresAt, string Currency, IReadOnlyList<CashChargeDto> Charges);
public record CollectCashRequest(decimal Amount, List<AllocationInput>? Allocations, string? Notes);
public record CashCollected(PaymentDto Payment, bool Replayed);

/// <summary>
/// Staff side of the cash QR. Resolving shows who is paying and what they owe and consumes nothing; collecting creates the real payment and
/// burns the token in one transaction. Both need a signed-in staff member: the code alone proves nothing.
/// </summary>
public class CashCollectionService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, PaymentService payments, IAuditWriter audit, IClock clock, OrgCalendar calendar, ICurrentUser user, ICurrentOrganization org, Notifier notifier)
{
    private const string BadCode = "Código inválido o vencido.";

    public async Task<Result<CashTokenInfo>> ResolveAsync(ResolveCashTokenRequest req, CancellationToken ct)
    {
        // Unknown, malformed, used, revoked and expired codes all answer the same, so a guess learns nothing.
        var code = CashToken.Normalize(req.Code);
        if (code is null) return new(Outcome.Invalid, Error: BadCode);
        var hash = CashToken.Hash(code);
        var token = await db.CashTokens.AsNoTracking().FirstOrDefaultAsync(t => t.CodeHash == hash, ct);
        if (token is null || !token.IsUsable(clock.UtcNow)) return new(Outcome.Invalid, Error: BadCode);

        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == token.TenantId).Select(t => new { t.FullName, t.Status }).FirstOrDefaultAsync(ct);
        if (tenant is null || tenant.Status != TenantStatus.Active) return new(Outcome.Invalid, Error: BadCode);

        var today = await calendar.TodayAsync(ct);
        var grace = await calendar.GraceDaysAsync(ct);
        var currency = await db.Organizations.AsNoTracking().Where(o => o.Id == org.Id).Select(o => o.Currency).FirstAsync(ct);
        var rows = await (from c in db.RentCharges.AsNoTracking()
                          join l in db.Leases on c.LeaseId equals l.Id
                          join u in db.Units on l.UnitId equals u.Id
                          join p in db.Properties on u.PropertyId equals p.Id
                          where l.TenantId == token.TenantId && c.Status != ChargeStatus.Voided
                          select new { c, UnitCode = u.Code, PropertyName = p.Name, Allocated = db.PaymentAllocations.Where(a => a.RentChargeId == c.Id && a.ReversedAt == null).Sum(a => (decimal?)a.Amount) ?? 0m })
                         .ToListAsync(ct);
        var charges = rows.Select(r => new { r, Balance = RentCharge.Balance(r.c.Amount, r.Allocated, voided: false) }).Where(x => x.Balance > 0)
            .OrderBy(x => x.r.c.DueDate).ThenBy(x => x.r.c.Id)
            .Select(x => new CashChargeDto(x.r.c.Id, x.r.c.Description, x.r.c.DueDate, x.r.c.Amount, x.Balance, RentCharge.Effective(x.r.c.Status, x.r.c.DueDate, today, grace), x.r.UnitCode, x.r.PropertyName)).ToList();
        return new(Outcome.Ok, new CashTokenInfo(token.Id, token.TenantId, tenant.FullName, token.ExpiresAt, currency, charges));
    }

    public async Task<Result<CashCollected>> CollectAsync(Guid tokenId, CollectCashRequest req, CancellationToken ct)
    {
        if (!await db.CashTokens.AnyAsync(t => t.Id == tokenId, ct)) return new(Outcome.NotFound);
        return await tx.RunAsync<Result<CashCollected>>(async () =>
        {
            // Two collectors scanning the same code: the second waits, then finds it already used.
            await locks.LockCashTokenAsync(tokenId, ct);
            var token = await db.CashTokens.FirstAsync(t => t.Id == tokenId, ct);
            var now = clock.UtcNow;
            if (token.UsedAt is null && !token.IsUsable(now)) return new(Outcome.Invalid, Error: BadCode);

            var today = await calendar.TodayAsync(ct);
            var created = await payments.CreateAsync(
                new CreatePaymentRequest(token.TenantId, today, req.Amount, PaymentMethod.Cash, "Cobro con QR", req.Notes, req.Allocations),
                $"cash-token:{token.Id}", ct); // the key is the token: the same code can never produce two payments
            if (created.Outcome == Outcome.Conflict && token.UsedAt is not null) return new(Outcome.Conflict, Error: "Este código ya se usó en otro cobro.");
            if (created.Outcome != Outcome.Ok) return new(created.Outcome, Error: created.Error);

            if (token.UsedAt is null)
            {
                token.UsedAt = now; token.UsedByUserId = user.UserId!.Value; token.PaymentId = created.Value!.Payment.Id;
                audit.Write("cash_token.used", nameof(CashToken), token.Id, metadata: new { token.TenantId, paymentId = token.PaymentId, req.Amount });
                var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == token.TenantId).Select(t => t.FullName).FirstAsync(ct);
                await notifier.ToTenantAsync(token.TenantId, NotificationText.CashCollected(tenantName, created.Value!.Payment.Amount), ct);
                await db.SaveChangesAsync(ct);
            }
            return new(Outcome.Ok, new CashCollected(created.Value!.Payment, created.Value.Replayed));
        }, ct);
    }
}
