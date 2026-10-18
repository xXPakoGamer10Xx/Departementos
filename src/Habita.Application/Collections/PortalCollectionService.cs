using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Notifications;
using Habita.Application.Properties;
using Habita.Domain.Collections;
using Habita.Domain.Organizations;
using Habita.Domain.Payments;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Collections;

public record PortalBankAccountDto(string HolderName, string BankName, string Clabe, string ClabeFormatted);
public record PortalPaymentInfoDto(string LandlordName, PortalBankAccountDto? Account);
public record ReportPaymentRequest(decimal Amount, DateOnly PaidOn, PaymentMethod Method, string? Reference, string? Note, Guid FileId);
/// <summary>A report as its tenant sees it: the comment from the administration, never who reviewed it.</summary>
public record TenantClaimDto(Guid Id, decimal Amount, DateOnly PaidOn, PaymentMethod Method, string? Reference, string? TenantNote, string FileName, ClaimStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, string? ReviewComment);
public record CashTokenCreatedDto(Guid Id, string Code, string QrPayload, DateTimeOffset ExpiresAt);

/// <summary>
/// What a tenant login may do around paying: see where to pay, report a payment with its proof, and ask for a cash QR.
/// Every query starts from the tenant of the signed-in membership; no request carries a tenant id.
/// </summary>
public class PortalCollectionService(IAppDbContext db, ICurrentOrganization org, ICurrentUser user, IDbTransactions tx, IFinanceLocks locks, BankAccountService accounts, OrgCalendar calendar, IAuditWriter audit, IClock clock, Notifier notifier)
{
    public const int MaxPageSize = 50;
    private Guid? TenantId => org.Role == Role.Tenant ? org.TenantId : null;

    public async Task<Result<PortalPaymentInfoDto>> PaymentInfoAsync(CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        var account = await accounts.ForTenantAsync(tid, ct);
        var landlord = await db.Settings.AsNoTracking().Select(s => s.LandlordName).FirstOrDefaultAsync(ct) ?? "";
        return new(Outcome.Ok, new PortalPaymentInfoDto(landlord, account is null ? null : new PortalBankAccountDto(account.HolderName, account.BankName, account.Clabe, ClabeRules.Format(account.Clabe))));
    }

    // ---------- payment reports ----------

    public async Task<Result<Paged<TenantClaimDto>>> ClaimsAsync(int page, int pageSize, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = from c in db.PaymentClaims.AsNoTracking() join f in db.Files on c.FileId equals f.Id where c.TenantId == tid select new { c, f.OriginalName };
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.c.CreatedAt).ThenBy(x => x.c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(Outcome.Ok, new Paged<TenantClaimDto>(rows.Select(r => ToDto(r.c, r.OriginalName)).ToList(), total, page, pageSize));
    }

    public async Task<Result<TenantClaimDto>> ReportAsync(ReportPaymentRequest req, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        var today = await calendar.TodayAsync(ct);
        var reference = Blank(req.Reference); var note = Blank(req.Note);
        if (!Enum.IsDefined(req.Method)) return new(Outcome.Invalid, Error: "El método de pago no es válido.");
        if (PaymentClaim.Validate(req.Amount, req.PaidOn, today, req.Method, reference, note) is { } error) return new(Outcome.Invalid, Error: error);

        return await tx.RunAsync<Result<TenantClaimDto>>(async () =>
        {
            await locks.LockScopeAsync($"claims:{tid}", ct); // the cap on pending reports holds even with two taps at once
            if (!await db.Tenants.AnyAsync(t => t.Id == tid && t.Status == TenantStatus.Active, ct)) return new(Outcome.Invalid, Error: "Tu registro de inquilino no está activo. Contacta a la administración.");
            // A proof must be the tenant's own upload. Anything else looks like a missing file, so ids of other people's files are not confirmed.
            var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == req.FileId && f.UploadedByUserId == user.UserId, ct);
            if (file is null) return new(Outcome.Invalid, Error: "Sube el comprobante antes de enviar el reporte.");
            if (await db.PaymentClaims.AnyAsync(c => c.FileId == file.Id, ct)) return new(Outcome.Conflict, Error: "Ese comprobante ya se usó en otro reporte. Sube el archivo de este pago.");
            var pending = await db.PaymentClaims.CountAsync(c => c.TenantId == tid && c.Status == ClaimStatus.Pending, ct);
            if (pending >= PaymentClaim.MaxPendingPerTenant) return new(Outcome.Conflict, Error: $"Ya tienes {PaymentClaim.MaxPendingPerTenant} reportes esperando revisión. Espera a que se atiendan.");

            var claim = new PaymentClaim
            {
                TenantId = tid, Amount = req.Amount, PaidOn = req.PaidOn, Method = req.Method, Reference = reference, TenantNote = note,
                FileId = file.Id, SubmittedByUserId = user.UserId!.Value, CreatedAt = clock.UtcNow,
            };
            db.PaymentClaims.Add(claim);
            audit.Write("payment_claim.created", nameof(PaymentClaim), claim.Id, metadata: new { claim.Amount, claim.PaidOn, claim.Method });
            var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == tid).Select(t => t.FullName).FirstAsync(ct);
            await notifier.ToStaffAsync(Audience.Reviewers, NotificationText.ClaimSubmitted(tenantName, claim.Amount, claim.Method, claim.PaidOn), ct);
            await db.SaveChangesAsync(ct);
            return new(Outcome.Ok, ToDto(claim, file.OriginalName));
        }, ct);
    }

    public async Task<Result<TenantClaimDto>> CancelClaimAsync(Guid id, CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        if (!await db.PaymentClaims.AnyAsync(c => c.Id == id && c.TenantId == tid, ct)) return new(Outcome.NotFound); // another tenant's report looks missing
        return await tx.RunAsync<Result<TenantClaimDto>>(async () =>
        {
            await locks.LockClaimAsync(id, ct);
            var claim = await db.PaymentClaims.FirstAsync(c => c.Id == id && c.TenantId == tid, ct);
            if (claim.Status != ClaimStatus.Pending) return new(Outcome.Conflict, Error: "Este reporte ya fue atendido y no se puede cancelar.");
            claim.Cancel(clock.UtcNow);
            audit.Write("payment_claim.cancelled", nameof(PaymentClaim), claim.Id);
            await db.SaveChangesAsync(ct);
            var name = await db.Files.AsNoTracking().Where(f => f.Id == claim.FileId).Select(f => f.OriginalName).FirstAsync(ct);
            return new(Outcome.Ok, ToDto(claim, name));
        }, ct);
    }

    // ---------- cash QR ----------

    public async Task<Result<CashTokenCreatedDto>> CreateCashTokenAsync(CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        return await tx.RunAsync<Result<CashTokenCreatedDto>>(async () =>
        {
            await locks.LockScopeAsync($"cash-token:{tid}", ct);
            if (!await db.Tenants.AnyAsync(t => t.Id == tid && t.Status == TenantStatus.Active, ct)) return new(Outcome.Invalid, Error: "Tu registro de inquilino no está activo. Contacta a la administración.");
            var now = clock.UtcNow;
            if (await db.CashTokens.CountAsync(t => t.TenantId == tid && t.CreatedAt > now.AddDays(-1), ct) >= CashToken.MaxPerDay)
                return new(Outcome.Conflict, Error: "Generaste demasiados códigos hoy. Inténtalo mañana o paga por transferencia.");

            await RevokeLiveAsync(tid, now, ct);
            var code = CashToken.Generate();
            var token = new CashToken { TenantId = tid, CodeHash = CashToken.Hash(code), CreatedByUserId = user.UserId!.Value, CreatedAt = now, ExpiresAt = now + CashToken.Lifetime };
            db.CashTokens.Add(token);
            audit.Write("cash_token.created", nameof(CashToken), token.Id, metadata: new { token.ExpiresAt }); // never the code
            await db.SaveChangesAsync(ct);
            return new(Outcome.Ok, new CashTokenCreatedDto(token.Id, CashToken.Format(code), CashToken.QrPayload(code), token.ExpiresAt));
        }, ct);
    }

    public async Task<Result<bool>> RevokeCashTokenAsync(CancellationToken ct)
    {
        if (TenantId is not { } tid) return new(Outcome.Forbidden);
        return await tx.RunAsync<Result<bool>>(async () =>
        {
            await locks.LockScopeAsync($"cash-token:{tid}", ct);
            await RevokeLiveAsync(tid, clock.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            return new(Outcome.Ok, true);
        }, ct);
    }

    /// <summary>Unused tokens that were never revoked, expired or not: a new code replaces them.</summary>
    private async Task RevokeLiveAsync(Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var old in await db.CashTokens.Where(t => t.TenantId == tenantId && t.UsedAt == null && t.RevokedAt == null).ToListAsync(ct))
        {
            old.RevokedAt = now;
            audit.Write("cash_token.revoked", nameof(CashToken), old.Id);
        }
    }

    private static TenantClaimDto ToDto(PaymentClaim c, string fileName) =>
        new(c.Id, c.Amount, c.PaidOn, c.Method, c.Reference, c.TenantNote, fileName, c.Status, c.CreatedAt, c.ReviewedAt, c.ReviewComment);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
