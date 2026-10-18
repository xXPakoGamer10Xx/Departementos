using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Notifications;
using Habita.Application.Payments;
using Habita.Application.Properties;
using Habita.Domain.Collections;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Collections;

public record ClaimFileDto(Guid Id, string Name, string ContentType, long SizeBytes);
/// <summary>A payment report as staff see it. DuplicateProof: the same proof file (by SHA-256) is attached to another report.</summary>
public record ClaimDto(Guid Id, Guid TenantId, string TenantName, decimal Amount, DateOnly PaidOn, Domain.Payments.PaymentMethod Method, string? Reference, string? TenantNote,
    ClaimFileDto File, ClaimStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, string? ReviewedByName, string? ReviewComment, Guid? PaymentId, bool DuplicateProof);
public record ConfirmClaimRequest(List<AllocationInput>? Allocations);
public record RejectClaimRequest(string? Comment);

/// <summary>Staff side of payment reports: read the queue, confirm (creates the real Payment) or reject with a comment the tenant reads.</summary>
public class PaymentClaimService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, PaymentService payments, IAuditWriter audit, IClock clock, ICurrentUser user, IUserDirectory users, Notifier notifier)
{
    public const int MaxPageSize = 100;

    public async Task<Paged<ClaimDto>> ListAsync(ClaimStatus? status, Guid? tenantId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = Rows();
        if (status is { } s) q = q.Where(x => x.c.Status == s);
        if (tenantId is { } t) q = q.Where(x => x.c.TenantId == t);
        var total = await q.CountAsync(ct);
        // Oldest pending first (a queue); everything else newest first.
        var ordered = status == ClaimStatus.Pending ? q.OrderBy(x => x.c.CreatedAt) : q.OrderByDescending(x => x.c.CreatedAt);
        var rows = await ordered.ThenBy(x => x.c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(await ToDtosAsync(rows, ct), total, page, pageSize);
    }

    public async Task<Result<ClaimDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await Rows().Where(x => x.c.Id == id).FirstOrDefaultAsync(ct);
        return row is null ? new(Outcome.NotFound) : new(Outcome.Ok, (await ToDtosAsync([row], ct))[0]);
    }

    public async Task<Result<ClaimDto>> ConfirmAsync(Guid id, ConfirmClaimRequest req, CancellationToken ct)
    {
        if (!await db.PaymentClaims.AnyAsync(c => c.Id == id, ct)) return new(Outcome.NotFound);
        return await tx.RunAsync<Result<ClaimDto>>(async () =>
        {
            // Two people confirming the same report: the second waits here, then sees it is no longer pending. Exactly one payment is created.
            await locks.LockClaimAsync(id, ct);
            var claim = await db.PaymentClaims.FirstAsync(c => c.Id == id, ct);
            if (claim.Status != ClaimStatus.Pending) return new(Outcome.Conflict, Error: "Este reporte ya fue atendido.");

            var notes = claim.TenantNote is null ? "Reporte de pago del inquilino." : $"Reporte de pago del inquilino: {claim.TenantNote}";
            var created = await payments.CreateAsync(
                new CreatePaymentRequest(claim.TenantId, claim.PaidOn, claim.Amount, claim.Method, claim.Reference, notes, req.Allocations),
                $"claim:{claim.Id}", ct); // the key ties the payment to this report: a retry can never make a second one
            if (created.Outcome != Outcome.Ok) return new(created.Outcome, Error: created.Error);

            claim.Confirm(created.Value!.Payment.Id, user.UserId!.Value, clock.UtcNow);
            audit.Write("payment_claim.confirmed", nameof(PaymentClaim), claim.Id, metadata: new { claim.TenantId, claim.Amount, paymentId = claim.PaymentId });
            await notifier.ToTenantAsync(claim.TenantId, NotificationText.ClaimConfirmed(await TenantNameAsync(claim.TenantId, ct), claim.Amount, claim.PaidOn), ct);
            await db.SaveChangesAsync(ct);
            return await GetAsync(id, ct);
        }, ct);
    }

    public async Task<Result<ClaimDto>> RejectAsync(Guid id, RejectClaimRequest req, CancellationToken ct)
    {
        if (PaymentClaim.ValidateRejection(req.Comment) is { } error) return new(Outcome.Invalid, Error: error);
        if (!await db.PaymentClaims.AnyAsync(c => c.Id == id, ct)) return new(Outcome.NotFound);
        return await tx.RunAsync<Result<ClaimDto>>(async () =>
        {
            await locks.LockClaimAsync(id, ct);
            var claim = await db.PaymentClaims.FirstAsync(c => c.Id == id, ct);
            if (claim.Status != ClaimStatus.Pending) return new(Outcome.Conflict, Error: "Este reporte ya fue atendido.");
            claim.Reject(req.Comment!, user.UserId!.Value, clock.UtcNow);
            audit.Write("payment_claim.rejected", nameof(PaymentClaim), claim.Id, claim.ReviewComment, new { claim.TenantId, claim.Amount });
            await notifier.ToTenantAsync(claim.TenantId, NotificationText.ClaimRejected(await TenantNameAsync(claim.TenantId, ct), claim.Amount, claim.ReviewComment!), ct);
            await db.SaveChangesAsync(ct);
            return await GetAsync(id, ct);
        }, ct);
    }

    private Task<string> TenantNameAsync(Guid tenantId, CancellationToken ct) => db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.FullName).FirstAsync(ct);

    private IQueryable<ClaimRow> Rows() =>
        from c in db.PaymentClaims.AsNoTracking()
        join t in db.Tenants on c.TenantId equals t.Id
        join f in db.Files on c.FileId equals f.Id
        select new ClaimRow { c = c, TenantName = t.FullName, f = f };

    private async Task<List<ClaimDto>> ToDtosAsync(List<ClaimRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var hashes = rows.Select(r => r.f.Sha256).Distinct().ToList();
        var duplicated = (await (from c in db.PaymentClaims.AsNoTracking() join f in db.Files on c.FileId equals f.Id
                                 where hashes.Contains(f.Sha256) group c by f.Sha256 into g where g.Count() > 1 select g.Key).ToListAsync(ct)).ToHashSet();
        var names = await users.NamesAsync(rows.Where(r => r.c.ReviewedByUserId is not null).Select(r => r.c.ReviewedByUserId!.Value), ct);
        return rows.Select(r => new ClaimDto(r.c.Id, r.c.TenantId, r.TenantName, r.c.Amount, r.c.PaidOn, r.c.Method, r.c.Reference, r.c.TenantNote,
            new ClaimFileDto(r.f.Id, r.f.OriginalName, r.f.ContentType, r.f.SizeBytes), r.c.Status, r.c.CreatedAt, r.c.ReviewedAt,
            r.c.ReviewedByUserId is { } u ? names.GetValueOrDefault(u) : null, r.c.ReviewComment, r.c.PaymentId, duplicated.Contains(r.f.Sha256))).ToList();
    }

    private class ClaimRow { public PaymentClaim c { get; set; } = null!; public string TenantName { get; set; } = ""; public Domain.Files.StoredFile f { get; set; } = null!; }
}
