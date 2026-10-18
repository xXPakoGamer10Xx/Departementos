using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Leases;
using Habita.Domain.Organizations;
using Habita.Domain.Properties;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Habita.Application.Properties;

public record PropertyDto(Guid Id, string Name, string Address, string? Notes, PropertyStatus Status, int UnitCount, DateTimeOffset CreatedAt);

public record UnitDto(
    Guid Id, Guid PropertyId, string Code, UnitType Type, int? Bedrooms,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? BaseRent,
    UnitStatus Status);

public record UnitSearchDto(Guid Id, string Code, Guid PropertyId, string PropertyName, UnitType Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? BaseRent);

public record Paged<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public record CreatePropertyRequest(string Name, string Address, string? Notes);
public record UpdatePropertyRequest(string? Name, string? Address, string? Notes, PropertyStatus? Status, string? Reason);
public record CreateUnitRequest(string Code, UnitType? Type, int? Bedrooms, decimal BaseRent);
public record UpdateUnitRequest(UnitType? Type, int? Bedrooms, decimal? BaseRent, UnitStatus? Status, string? Reason);

public class PropertyService(IAppDbContext db, ICurrentOrganization org, IAuditWriter audit, IClock clock, OrgCalendar calendar)
{
    public const int MaxPageSize = 100;

    // ---------- Properties ----------

    public async Task<Paged<PropertyDto>> ListPropertiesAsync(string? search, PropertyStatus? status, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Clamp(page, pageSize);
        var q = db.Properties.AsQueryable();
        if (status is { } s) q = q.Where(p => p.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(p => p.Name.ToLower().Contains(term) || p.Address.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(p => p.Name).ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PropertyDto(p.Id, p.Name, p.Address, p.Notes, p.Status,
                db.Units.Count(u => u.PropertyId == p.Id && u.Status != UnitStatus.Archived), p.CreatedAt))
            .ToListAsync(ct);
        return new(items, total, page, pageSize);
    }

    public async Task<Result<PropertyDto>> GetPropertyAsync(Guid id, CancellationToken ct)
    {
        var dto = await db.Properties.Where(p => p.Id == id)
            .Select(p => new PropertyDto(p.Id, p.Name, p.Address, p.Notes, p.Status,
                db.Units.Count(u => u.PropertyId == p.Id && u.Status != UnitStatus.Archived), p.CreatedAt))
            .FirstOrDefaultAsync(ct);
        return dto is null ? new(Outcome.NotFound) : new(Outcome.Ok, dto);
    }

    public async Task<Result<PropertyDto>> CreatePropertyAsync(CreatePropertyRequest req, CancellationToken ct)
    {
        if (Validate(req.Name, req.Address, req.Notes, requireText: true) is { } error) return new(Outcome.Invalid, Error: error);
        var now = clock.UtcNow;
        var p = new Property { Name = req.Name.Trim(), Address = req.Address.Trim(), Notes = NullIfBlank(req.Notes), CreatedAt = now, UpdatedAt = now };
        db.Properties.Add(p);
        audit.Write("property.created", nameof(Property), p.Id, metadata: new { p.Name, p.Address });
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, new PropertyDto(p.Id, p.Name, p.Address, p.Notes, p.Status, 0, p.CreatedAt));
    }

    public async Task<Result<PropertyDto>> UpdatePropertyAsync(Guid id, UpdatePropertyRequest req, CancellationToken ct)
    {
        var p = await db.Properties.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return new(Outcome.NotFound);
        if (Validate(req.Name, req.Address, req.Notes, requireText: false) is { } error) return new(Outcome.Invalid, Error: error);

        var before = new { p.Name, p.Address, p.Notes };
        if (req.Name is not null) p.Name = req.Name.Trim();
        if (req.Address is not null) p.Address = req.Address.Trim();
        if (req.Notes is not null) p.Notes = NullIfBlank(req.Notes);
        p.UpdatedAt = clock.UtcNow;
        if (before.Name != p.Name || before.Address != p.Address || before.Notes != p.Notes)
            audit.Write("property.updated", nameof(Property), p.Id, metadata: new { before, after = new { p.Name, p.Address, p.Notes } });

        if (req.Status is { } status && status != p.Status)
        {
            p.Status = status;
            if (status == PropertyStatus.Archived)
            {
                if (await db.Leases.AnyAsync(l => l.Status == LeaseStatus.Active && db.Units.Any(u => u.Id == l.UnitId && u.PropertyId == p.Id), ct))
                    return new(Outcome.Conflict, Error: "La propiedad tiene contratos vigentes. Termínalos o cancélalos antes de archivarla.");
                // Archiving keeps history: units are archived with it (restoring the property leaves them archived).
                var units = await db.Units.Where(u => u.PropertyId == p.Id && u.Status != UnitStatus.Archived).ToListAsync(ct);
                foreach (var u in units) { u.Status = UnitStatus.Archived; u.UpdatedAt = p.UpdatedAt; }
                audit.Write("property.archived", nameof(Property), p.Id, req.Reason, new { unitsArchived = units.Count });
            }
            else audit.Write("property.restored", nameof(Property), p.Id, req.Reason);
        }
        await db.SaveChangesAsync(ct);
        return await GetPropertyAsync(id, ct);
    }

    // ---------- Units ----------

    public async Task<Result<Paged<UnitDto>>> ListUnitsAsync(Guid propertyId, string? search, UnitStatus? status, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Properties.AnyAsync(p => p.Id == propertyId, ct)) return new(Outcome.NotFound);
        (page, pageSize) = Clamp(page, pageSize);
        var today = await calendar.TodayAsync(ct);
        var q = db.Units.Where(u => u.PropertyId == propertyId);
        // Occupied is derived: an Available unit with a lease in force today. It is never stored.
        if (status == UnitStatus.Occupied) q = q.Where(u => u.Status == UnitStatus.Available && db.Leases.Any(l => l.UnitId == u.Id
            && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended) && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)));
        else if (status == UnitStatus.Available) q = q.Where(u => u.Status == UnitStatus.Available && !db.Leases.Any(l => l.UnitId == u.Id
            && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended) && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)));
        else if (status is { } s) q = q.Where(u => u.Status == s);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); q = q.Where(u => u.Code.ToLower().Contains(term)); }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(u => u.Code).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var occupied = await db.Leases.Where(l => ids.Contains(l.UnitId) && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Ended)
                && l.StartDate <= today && (l.EndDate == null || l.EndDate >= today)).Select(l => l.UnitId).Distinct().ToListAsync(ct);
        return new(Outcome.Ok, new Paged<UnitDto>(rows.Select(r => ToDto(r, occupied.Contains(r.Id))).ToList(), total, page, pageSize));
    }

    /// <summary>Org-wide unit lookup for pickers (e.g. when drafting a lease).</summary>
    public async Task<Paged<UnitSearchDto>> SearchUnitsAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = from u in db.Units join p in db.Properties on u.PropertyId equals p.Id
                where u.Status != UnitStatus.Archived && p.Status == PropertyStatus.Active select new { u, p };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(x => x.u.Code.ToLower().Contains(term) || x.p.Name.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.p.Name).ThenBy(x => x.u.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(x => new UnitSearchDto(x.u.Id, x.u.Code, x.p.Id, x.p.Name, x.u.Type, org.Role == Role.Maintenance ? null : x.u.BaseRent)).ToList(), total, page, pageSize);
    }

    public async Task<Result<UnitDto>> CreateUnitAsync(Guid propertyId, CreateUnitRequest req, CancellationToken ct)
    {
        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, ct);
        if (property is null) return new(Outcome.NotFound);
        if (property.Status == PropertyStatus.Archived) return new(Outcome.Conflict, Error: "La propiedad está archivada y no admite unidades nuevas.");
        if (ValidateUnit(req.Code, req.Bedrooms, req.BaseRent) is { } error) return new(Outcome.Invalid, Error: error);

        var code = Unit.NormalizeCode(req.Code);
        if (await db.Units.AnyAsync(u => u.PropertyId == propertyId && u.Code == code, ct))
            return new(Outcome.Conflict, Error: $"Ya existe una unidad con el código {code} en esta propiedad.");

        var now = clock.UtcNow;
        var u = new Unit { PropertyId = propertyId, Code = code, Type = req.Type ?? UnitType.Apartment, Bedrooms = req.Bedrooms, BaseRent = req.BaseRent, CreatedAt = now, UpdatedAt = now };
        db.Units.Add(u);
        audit.Write("unit.created", nameof(Unit), u.Id, metadata: new { propertyId, u.Code, u.Type, u.BaseRent });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) // lost a race against a concurrent insert of the same code
        {
            return new(Outcome.Conflict, Error: $"Ya existe una unidad con el código {code} en esta propiedad.");
        }
        return new(Outcome.Ok, ToDto(u));
    }

    public async Task<Result<UnitDto>> UpdateUnitAsync(Guid propertyId, Guid unitId, UpdateUnitRequest req, CancellationToken ct)
    {
        var u = await db.Units.FirstOrDefaultAsync(x => x.Id == unitId && x.PropertyId == propertyId, ct);
        if (u is null) return new(Outcome.NotFound);
        if (req.Status == UnitStatus.Occupied) return new(Outcome.Invalid, Error: "La ocupación se calcula a partir de los contratos vigentes; no se asigna manualmente.");
        if (req.Status == UnitStatus.Archived && await db.Leases.AnyAsync(l => l.UnitId == u.Id && l.Status == LeaseStatus.Active, ct))
            return new(Outcome.Conflict, Error: "La unidad tiene un contrato vigente. Termínalo o cancélalo antes de archivarla.");
        if (req.BaseRent is not null || req.Bedrooms is not null)
            if (ValidateUnit("x", req.Bedrooms ?? u.Bedrooms, req.BaseRent ?? u.BaseRent) is { } error) return new(Outcome.Invalid, Error: error);

        if (req.Status == UnitStatus.Available || req.Status == UnitStatus.Maintenance)
        {
            var parent = await db.Properties.FirstAsync(p => p.Id == propertyId, ct);
            if (parent.Status == PropertyStatus.Archived && u.Status == UnitStatus.Archived)
                return new(Outcome.Conflict, Error: "Restaura primero la propiedad para reactivar sus unidades.");
        }

        var before = new { u.Type, u.Bedrooms, u.BaseRent, u.Status };
        if (req.Type is { } t) u.Type = t;
        if (req.Bedrooms is { } b) u.Bedrooms = b;
        if (req.BaseRent is { } r) u.BaseRent = r;
        if (req.Status is { } s) u.Status = s;
        u.UpdatedAt = clock.UtcNow;
        audit.Write(u.Status != before.Status && u.Status == UnitStatus.Archived ? "unit.archived" : "unit.updated", nameof(Unit), u.Id, req.Reason,
            new { before, after = new { u.Type, u.Bedrooms, u.BaseRent, u.Status } });
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, ToDto(u));
    }

    // ---------- helpers ----------

    // Maintenance may see unit location data but never rent amounts.
    private UnitDto ToDto(Unit u, bool occupied = false) =>
        new(u.Id, u.PropertyId, u.Code, u.Type, u.Bedrooms, org.Role == Role.Maintenance ? null : u.BaseRent,
            occupied && u.Status == UnitStatus.Available ? UnitStatus.Occupied : u.Status);

    private static (int, int) Clamp(int page, int pageSize) => (Math.Max(page, 1), Math.Clamp(pageSize, 1, MaxPageSize));
    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Validate(string? name, string? address, string? notes, bool requireText)
    {
        if (requireText && string.IsNullOrWhiteSpace(name)) return "El nombre es obligatorio.";
        if (requireText && string.IsNullOrWhiteSpace(address)) return "La dirección es obligatoria.";
        if (name is not null && (name.Trim().Length == 0 || name.Trim().Length > 200)) return "El nombre debe tener entre 1 y 200 caracteres.";
        if (address is not null && (address.Trim().Length == 0 || address.Trim().Length > 500)) return "La dirección debe tener entre 1 y 500 caracteres.";
        if (notes is not null && notes.Length > 2000) return "Las notas no pueden superar 2000 caracteres.";
        return null;
    }

    private static string? ValidateUnit(string code, int? bedrooms, decimal baseRent)
    {
        var c = code.Trim();
        if (c.Length == 0 || c.Length > 30) return "El código debe tener entre 1 y 30 caracteres.";
        if (bedrooms is < 0 or > Unit.MaxBedrooms) return $"Las recámaras deben estar entre 0 y {Unit.MaxBedrooms}.";
        if (baseRent < 0 || baseRent > Unit.MaxRent) return "La renta base debe ser un importe entre 0 y 99,999,999.99.";
        if (decimal.Round(baseRent, 2) != baseRent) return "La renta base admite máximo 2 decimales.";
        return null;
    }
}
