using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Domain.Common;
using Habita.Domain.Leases;
using Habita.Domain.Properties;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Leases;

public record LeaseDto(
    Guid Id, Guid UnitId, string UnitCode, Guid PropertyId, string PropertyName, Guid TenantId, string TenantName,
    DateOnly StartDate, DateOnly? EndDate, decimal MonthlyRent, int DueDay, decimal? DepositAmount,
    LeaseStatus Status, string? TermsNotes, string? ClosureReason, bool IsCurrent, DateTimeOffset CreatedAt, string? GuarantorName = null, string? GuarantorPhone = null);

public record CreateLeaseRequest(Guid UnitId, Guid TenantId, DateOnly StartDate, DateOnly? EndDate, decimal MonthlyRent, int DueDay, decimal? DepositAmount, string? TermsNotes, string? GuarantorName = null, string? GuarantorPhone = null);
/// <summary>Draft: any field. Active: only MonthlyRent, DueDay, DepositAmount and TermsNotes (never rewrites past charges).</summary>
public record UpdateLeaseRequest(Guid? UnitId, Guid? TenantId, DateOnly? StartDate, DateOnly? EndDate, bool? ClearEndDate, decimal? MonthlyRent, int? DueDay, decimal? DepositAmount, string? TermsNotes, string? GuarantorName = null, string? GuarantorPhone = null);
public record TerminateLeaseRequest(DateOnly EndDate, string Reason);
public record CancelLeaseRequest(string Reason);

public class LeaseService(IAppDbContext db, IAuditWriter audit, IClock clock, OrgCalendar calendar)
{
    public const int MaxPageSize = 100;

    // ---------- queries ----------

    private IQueryable<LeaseDto> Project(IQueryable<Lease> q, DateOnly today) =>
        from l in q
        join u in db.Units on l.UnitId equals u.Id
        join p in db.Properties on u.PropertyId equals p.Id
        join t in db.Tenants on l.TenantId equals t.Id
        select new LeaseDto(l.Id, l.UnitId, u.Code, p.Id, p.Name, l.TenantId, t.FullName, l.StartDate, l.EndDate, l.MonthlyRent, l.DueDay,
            l.DepositAmount, l.Status, l.TermsNotes, l.ClosureReason,
            (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended) && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today),
            l.CreatedAt, l.GuarantorName, l.GuarantorPhone);

    public async Task<Paged<LeaseDto>> ListAsync(LeaseStatus? status, Guid? unitId, Guid? tenantId, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var today = await calendar.TodayAsync(ct);
        // Filter and order on the joined rows, project to the DTO last (EF cannot compose over a constructor projection).
        var q = from l in db.Leases
                join u in db.Units on l.UnitId equals u.Id
                join p in db.Properties on u.PropertyId equals p.Id
                join t in db.Tenants on l.TenantId equals t.Id
                select new { l, u, p, t };
        if (status is { } s) q = q.Where(x => x.l.Status == s);
        if (unitId is { } uid) q = q.Where(x => x.l.UnitId == uid);
        if (tenantId is { } tid) q = q.Where(x => x.l.TenantId == tid);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(x => x.t.FullName.ToLower().Contains(term) || x.u.Code.ToLower().Contains(term) || x.p.Name.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.l.StartDate).ThenBy(x => x.l.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(x => new LeaseDto(x.l.Id, x.l.UnitId, x.u.Code, x.p.Id, x.p.Name, x.l.TenantId, x.t.FullName, x.l.StartDate, x.l.EndDate,
            x.l.MonthlyRent, x.l.DueDay, x.l.DepositAmount, x.l.Status, x.l.TermsNotes, x.l.ClosureReason, x.l.IsCurrent(today), x.l.CreatedAt, x.l.GuarantorName, x.l.GuarantorPhone)).ToList();
        return new(items, total, page, pageSize);
    }

    public async Task<Result<LeaseDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var today = await calendar.TodayAsync(ct);
        var dto = await Project(db.Leases.Where(l => l.Id == id), today).FirstOrDefaultAsync(ct);
        return dto is null ? new(Outcome.NotFound) : new(Outcome.Ok, dto);
    }

    // ---------- commands ----------

    public async Task<Result<LeaseDto>> CreateAsync(CreateLeaseRequest req, CancellationToken ct)
    {
        if (Lease.ValidateTerms(req.StartDate, req.EndDate, req.MonthlyRent, req.DueDay, req.DepositAmount, req.TermsNotes) is { } error)
            return new(Outcome.Invalid, Error: error);
        if (Lease.ValidateGuarantor(Blank(req.GuarantorName), Blank(req.GuarantorPhone)) is { } guarantorError) return new(Outcome.Invalid, Error: guarantorError);
        if (await CheckReferencesAsync(req.UnitId, req.TenantId, ct) is { } refError) return refError;

        var now = clock.UtcNow;
        var l = new Lease
        {
            UnitId = req.UnitId, TenantId = req.TenantId, StartDate = req.StartDate, EndDate = req.EndDate, MonthlyRent = req.MonthlyRent,
            DueDay = req.DueDay, DepositAmount = req.DepositAmount, TermsNotes = Blank(req.TermsNotes), CreatedAt = now, UpdatedAt = now,
            GuarantorName = Blank(req.GuarantorName), GuarantorPhone = Blank(req.GuarantorPhone),
        };
        db.Leases.Add(l);
        audit.Write("lease.created", nameof(Lease), l.Id, metadata: new { l.UnitId, l.TenantId, l.StartDate, l.EndDate, l.MonthlyRent, l.DueDay });
        await db.SaveChangesAsync(ct);
        return await GetAsync(l.Id, ct);
    }

    public async Task<Result<LeaseDto>> UpdateAsync(Guid id, UpdateLeaseRequest req, CancellationToken ct)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return new(Outcome.NotFound);
        var structural = req.UnitId is not null || req.TenantId is not null || req.StartDate is not null || req.EndDate is not null || req.ClearEndDate == true;
        switch (l.Status)
        {
            case LeaseStatus.Draft: break;
            case LeaseStatus.Active when !structural: break;
            case LeaseStatus.Active: return new(Outcome.Conflict, Error: "En un contrato vigente solo pueden cambiarse la renta, el día de vencimiento, el depósito y las condiciones.");
            default: return new(Outcome.Conflict, Error: "Un contrato terminado o cancelado no puede editarse.");
        }

        var unitId = req.UnitId ?? l.UnitId; var tenantId = req.TenantId ?? l.TenantId;
        var start = req.StartDate ?? l.StartDate;
        var end = req.ClearEndDate == true ? null : req.EndDate ?? l.EndDate;
        var rent = req.MonthlyRent ?? l.MonthlyRent; var dueDay = req.DueDay ?? l.DueDay;
        var deposit = req.DepositAmount ?? l.DepositAmount;
        var notes = req.TermsNotes is null ? l.TermsNotes : Blank(req.TermsNotes);
        if (Lease.ValidateTerms(start, end, rent, dueDay, deposit, notes) is { } error) return new(Outcome.Invalid, Error: error);
        // Empty text clears the guarantor; absent leaves it as it is.
        var guarantorName = req.GuarantorName is null ? l.GuarantorName : Blank(req.GuarantorName);
        var guarantorPhone = req.GuarantorPhone is null ? l.GuarantorPhone : Blank(req.GuarantorPhone);
        if (guarantorName is null)
        {
            // Clearing the name clears the phone with it, but a phone sent with no name to go with it is a mistake worth saying so.
            if (Blank(req.GuarantorPhone) is not null) return new(Outcome.Invalid, Error: "Indica el nombre del fiador junto con su teléfono.");
            guarantorPhone = null;
        }
        if (Lease.ValidateGuarantor(guarantorName, guarantorPhone) is { } guarantorError) return new(Outcome.Invalid, Error: guarantorError);
        if ((unitId != l.UnitId || tenantId != l.TenantId) && await CheckReferencesAsync(unitId, tenantId, ct) is { } refError) return refError;

        var before = new { l.UnitId, l.TenantId, l.StartDate, l.EndDate, l.MonthlyRent, l.DueDay, l.DepositAmount };
        (l.UnitId, l.TenantId, l.StartDate, l.EndDate, l.MonthlyRent, l.DueDay, l.DepositAmount, l.TermsNotes) = (unitId, tenantId, start, end, rent, dueDay, deposit, notes);
        var guarantorChanged = guarantorName != l.GuarantorName || guarantorPhone != l.GuarantorPhone;
        (l.GuarantorName, l.GuarantorPhone) = (guarantorName, guarantorPhone);
        l.UpdatedAt = clock.UtcNow;
        audit.Write("lease.updated", nameof(Lease), l.Id, metadata: new { before, after = new { l.UnitId, l.TenantId, l.StartDate, l.EndDate, l.MonthlyRent, l.DueDay, l.DepositAmount }, guarantorChanged }); // never the guarantor's own data
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<LeaseDto>> ActivateAsync(Guid id, CancellationToken ct)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return new(Outcome.NotFound);
        if (l.Status != LeaseStatus.Draft) return new(Outcome.Conflict, Error: "Solo un contrato en borrador puede activarse.");
        if (await CheckReferencesAsync(l.UnitId, l.TenantId, ct) is { } refError) return new(refError.Outcome == Outcome.Invalid ? Outcome.Conflict : refError.Outcome, Error: refError.Error);
        if (await OverlapsAsync(l, ct)) return new(Outcome.Conflict, Error: OverlapMessage);

        l.Activate();
        l.UpdatedAt = clock.UtcNow;
        audit.Write("lease.activated", nameof(Lease), l.Id, metadata: new { l.UnitId, l.StartDate, l.EndDate });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Lost a race: the database exclusion constraint rejected the overlap. Anything else is a real failure.
            if (await OverlapsAsync(l, ct)) return new(Outcome.Conflict, Error: OverlapMessage);
            throw;
        }
        return await GetAsync(id, ct);
    }

    public async Task<Result<LeaseDto>> TerminateAsync(Guid id, TerminateLeaseRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) return new(Outcome.Invalid, Error: "El motivo de la terminación es obligatorio.");
        if (req.Reason.Trim().Length > 1000) return new(Outcome.Invalid, Error: "El motivo no puede superar 1000 caracteres.");
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return new(Outcome.NotFound);
        try { l.Terminate(req.EndDate, req.Reason.Trim()); }
        catch (DomainException e) { return new(e.Code == "lease.invalid_date" ? Outcome.Invalid : Outcome.Conflict, Error: e.Message); }
        l.UpdatedAt = clock.UtcNow;
        audit.Write("lease.terminated", nameof(Lease), l.Id, l.ClosureReason, new { endDate = req.EndDate });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<LeaseDto>> CancelAsync(Guid id, CancelLeaseRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Reason)) return new(Outcome.Invalid, Error: "El motivo de la cancelación es obligatorio.");
        if (req.Reason.Trim().Length > 1000) return new(Outcome.Invalid, Error: "El motivo no puede superar 1000 caracteres.");
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return new(Outcome.NotFound);
        try { l.Cancel(req.Reason.Trim()); }
        catch (DomainException e) { return new(Outcome.Conflict, Error: e.Message); }
        l.UpdatedAt = clock.UtcNow;
        audit.Write("lease.cancelled", nameof(Lease), l.Id, l.ClosureReason);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- helpers ----------

    private const string OverlapMessage = "La unidad ya tiene un contrato vigente que se solapa con estas fechas.";

    private Task<bool> OverlapsAsync(Lease l, CancellationToken ct)
    {
        var end = l.EndDate ?? DateOnly.MaxValue;
        return db.Leases.AnyAsync(o => o.UnitId == l.UnitId && o.Id != l.Id
            && (o.Status == LeaseStatus.Active || o.Status == LeaseStatus.Ended)
            && o.StartDate <= end && (o.EndDate == null || o.EndDate >= l.StartDate), ct);
    }

    /// <summary>The unit and tenant must exist in this organization (other organizations' ids look nonexistent) and be usable.</summary>
    private async Task<Result<LeaseDto>?> CheckReferencesAsync(Guid unitId, Guid tenantId, CancellationToken ct)
    {
        var unit = await db.Units.Where(u => u.Id == unitId)
            .Select(u => new { u.Status, PropertyStatus = db.Properties.Where(p => p.Id == u.PropertyId).Select(p => p.Status).First() })
            .FirstOrDefaultAsync(ct);
        if (unit is null) return new(Outcome.Invalid, Error: "La unidad no existe.");
        if (unit.Status == UnitStatus.Archived || unit.PropertyStatus == PropertyStatus.Archived)
            return new(Outcome.Conflict, Error: "La unidad o su propiedad están archivadas y no admiten contratos.");
        var tenantStatus = await db.Tenants.Where(t => t.Id == tenantId).Select(t => (TenantStatus?)t.Status).FirstOrDefaultAsync(ct);
        if (tenantStatus is null) return new(Outcome.Invalid, Error: "El inquilino no existe.");
        if (tenantStatus == TenantStatus.Archived) return new(Outcome.Conflict, Error: "El inquilino está archivado y no admite contratos.");
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
