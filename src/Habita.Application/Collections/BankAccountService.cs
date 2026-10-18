using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Collections;
using Habita.Domain.Leases;
using Habita.Domain.Properties;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Collections;

public record BankAccountDto(Guid Id, Guid? PropertyId, string? PropertyName, string HolderName, string BankName, string Clabe, string ClabeFormatted, bool IsDefault, BankAccountStatus Status);
public record CreateBankAccountRequest(string? HolderName, string? Clabe, Guid? PropertyId, bool? IsDefault);
/// <summary>Absent field = unchanged.</summary>
public record UpdateBankAccountRequest(string? HolderName, string? Clabe, bool? IsDefault);

public class BankAccountService(IAppDbContext db, IDbTransactions tx, IFinanceLocks locks, IAuditWriter audit, IClock clock, OrgCalendar calendar)
{
    private const string Scope = "bank-accounts";

    public async Task<List<BankAccountDto>> ListAsync(bool includeArchived, CancellationToken ct)
    {
        var q = from a in db.BankAccounts.AsNoTracking()
                join p in db.Properties on a.PropertyId equals p.Id into props
                from p in props.DefaultIfEmpty()
                where includeArchived || a.Status == BankAccountStatus.Active
                orderby a.Status, a.PropertyId != null, a.CreatedAt
                select new { a, PropertyName = p != null ? p.Name : null };
        return (await q.ToListAsync(ct)).Select(x => ToDto(x.a, x.PropertyName)).ToList();
    }

    public async Task<Result<BankAccountDto>> CreateAsync(CreateBankAccountRequest req, CancellationToken ct)
    {
        var holder = req.HolderName?.Trim(); var clabe = ClabeRules.Normalize(req.Clabe);
        if (BankAccount.ValidateHolder(holder) is { } holderError) return new(Outcome.Invalid, Error: holderError);
        if (BankAccount.ValidateClabe(clabe) is { } clabeError) return new(Outcome.Invalid, Error: clabeError);
        if (req.PropertyId is not null && req.IsDefault == true) return new(Outcome.Invalid, Error: "Solo una cuenta general puede ser la predeterminada.");

        return await tx.RunAsync<Result<BankAccountDto>>(async () =>
        {
            await locks.LockScopeAsync(Scope, ct); // two admins creating accounts at once cannot both become the default
            string? propertyName = null;
            if (req.PropertyId is { } pid)
            {
                var property = await db.Properties.AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.Name, p.Status }).FirstOrDefaultAsync(ct);
                if (property is null) return new(Outcome.NotFound);
                if (property.Status != PropertyStatus.Active) return new(Outcome.Invalid, Error: "La propiedad está archivada.");
                if (await db.BankAccounts.AnyAsync(a => a.PropertyId == pid && a.Status == BankAccountStatus.Active, ct))
                    return new(Outcome.Conflict, Error: "Esa propiedad ya tiene una cuenta activa. Archívala antes de dar de alta otra.");
                propertyName = property.Name;
            }

            var now = clock.UtcNow;
            var account = new BankAccount
            {
                PropertyId = req.PropertyId, HolderName = holder!, Clabe = clabe!, BankName = ClabeRules.BankName(clabe!) ?? BankAccount.UnknownBank,
                CreatedAt = now, UpdatedAt = now,
            };
            if (req.PropertyId is null)
            {
                var hasDefault = await db.BankAccounts.AnyAsync(a => a.PropertyId == null && a.Status == BankAccountStatus.Active && a.IsDefault, ct);
                account.IsDefault = !hasDefault || req.IsDefault == true;
                if (account.IsDefault && hasDefault) await ClearDefaultAsync(now, ct);
            }
            db.BankAccounts.Add(account);
            audit.Write("bank_account.created", nameof(BankAccount), account.Id, metadata: new { account.PropertyId, account.BankName, last4 = account.Clabe[^4..], account.IsDefault }); // never the full CLABE
            await db.SaveChangesAsync(ct);
            return new(Outcome.Ok, ToDto(account, propertyName));
        }, ct);
    }

    public async Task<Result<BankAccountDto>> UpdateAsync(Guid id, UpdateBankAccountRequest req, CancellationToken ct)
    {
        var holder = req.HolderName?.Trim(); var clabe = ClabeRules.Normalize(req.Clabe);
        if (req.HolderName is not null && BankAccount.ValidateHolder(holder) is { } holderError) return new(Outcome.Invalid, Error: holderError);
        if (req.Clabe is not null && BankAccount.ValidateClabe(clabe) is { } clabeError) return new(Outcome.Invalid, Error: clabeError);

        return await tx.RunAsync<Result<BankAccountDto>>(async () =>
        {
            await locks.LockScopeAsync(Scope, ct);
            var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (account is null) return new(Outcome.NotFound);
            if (account.Status == BankAccountStatus.Archived) return new(Outcome.Conflict, Error: "La cuenta está archivada.");
            if (req.IsDefault == true && account.PropertyId is not null) return new(Outcome.Invalid, Error: "Solo una cuenta general puede ser la predeterminada.");
            if (req.IsDefault == false && account.IsDefault) return new(Outcome.Invalid, Error: "Elige otra cuenta como predeterminada en lugar de quitar esta.");

            var now = clock.UtcNow; var changed = new List<string>();
            if (req.HolderName is not null && holder != account.HolderName) { account.HolderName = holder!; changed.Add("holderName"); }
            if (req.Clabe is not null && clabe != account.Clabe)
            {
                account.Clabe = clabe!; account.BankName = ClabeRules.BankName(clabe!) ?? BankAccount.UnknownBank; changed.Add("clabe");
            }
            if (req.IsDefault == true && !account.IsDefault)
            {
                await ClearDefaultAsync(now, ct); // saved first: the unique index allows only one default at a time
                account.IsDefault = true; changed.Add("isDefault");
            }
            if (changed.Count > 0)
            {
                account.UpdatedAt = now;
                audit.Write("bank_account.updated", nameof(BankAccount), account.Id, metadata: new { fields = changed });
                await db.SaveChangesAsync(ct);
            }
            var propertyName = account.PropertyId is { } pid ? await db.Properties.Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
            return new(Outcome.Ok, ToDto(account, propertyName));
        }, ct);
    }

    public async Task<Result<BankAccountDto>> ArchiveAsync(Guid id, CancellationToken ct) =>
        await tx.RunAsync<Result<BankAccountDto>>(async () =>
        {
            await locks.LockScopeAsync(Scope, ct);
            var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (account is null) return new(Outcome.NotFound);
            if (account.Status == BankAccountStatus.Active)
            {
                if (account.IsDefault && await db.BankAccounts.AnyAsync(a => a.Id != id && a.PropertyId == null && a.Status == BankAccountStatus.Active, ct))
                    return new(Outcome.Conflict, Error: "Es la cuenta predeterminada. Elige otra como predeterminada antes de archivarla.");
                account.Status = BankAccountStatus.Archived; account.IsDefault = false; account.UpdatedAt = clock.UtcNow;
                audit.Write("bank_account.archived", nameof(BankAccount), account.Id);
                await db.SaveChangesAsync(ct);
            }
            return new(Outcome.Ok, ToDto(account, null));
        }, ct);

    /// <summary>The account a tenant pays into: the one of the property of their current lease, else the organization's default. Null when none is set up.</summary>
    public async Task<BankAccount?> ForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var propertyId = await (from l in db.Leases.AsNoTracking()
                                join u in db.Units on l.UnitId equals u.Id
                                where l.TenantId == tenantId && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended)
                                      && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)
                                orderby l.StartDate descending
                                select (Guid?)u.PropertyId).FirstOrDefaultAsync(ct);
        if (propertyId is { } pid)
        {
            var own = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.PropertyId == pid && a.Status == BankAccountStatus.Active, ct);
            if (own is not null) return own;
        }
        return await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.PropertyId == null && a.IsDefault && a.Status == BankAccountStatus.Active, ct);
    }

    private async Task ClearDefaultAsync(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var other in await db.BankAccounts.Where(a => a.IsDefault).ToListAsync(ct)) { other.IsDefault = false; other.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
    }

    private static BankAccountDto ToDto(BankAccount a, string? propertyName) =>
        new(a.Id, a.PropertyId, propertyName, a.HolderName, a.BankName, a.Clabe, ClabeRules.Format(a.Clabe), a.IsDefault, a.Status);
}
