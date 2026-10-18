using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Domain.Leases;
using Habita.Domain.Organizations;
using Habita.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Tenants;

public record TenantDto(Guid Id, string FullName, string? Email, string? Phone, string? Notes, TenantStatus Status, DateTimeOffset CreatedAt);
public record CreateTenantRequest(string FullName, string? Email, string? Phone, string? Notes);
/// <summary>Absent field = unchanged; empty string clears Email, Phone or Notes.</summary>
public record UpdateTenantRequest(string? FullName, string? Email, string? Phone, string? Notes, TenantStatus? Status, string? Reason);

public class TenantService(IAppDbContext db, ICurrentOrganization org, IAuditWriter audit, IClock clock)
{
    /// <summary>The Collector finds who is paying but does not get contact details or private notes (data minimization).</summary>
    private bool Minimal => org.Role == Role.Collector;
    private TenantDto Scope(TenantDto t) => Minimal ? t with { Email = null, Notes = null } : t;

    public const int MaxPageSize = 100;

    public async Task<Paged<TenantDto>> ListAsync(string? search, TenantStatus? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = db.Tenants.AsQueryable();
        if (status is { } s) q = q.Where(t => t.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            // A Collector must not be able to probe emails or phones through search, so for them only the name matches.
            q = Minimal
                ? q.Where(t => t.FullName.ToLower().Contains(term))
                : q.Where(t => t.FullName.ToLower().Contains(term)
                               || (t.Email != null && t.Email.Contains(term))
                               || (t.Phone != null && t.Phone.Contains(term)));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(t => t.FullName).ThenBy(t => t.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => ToDto(t)).ToListAsync(ct);
        return new(items.Select(Scope).ToList(), total, page, pageSize);
    }

    public async Task<Result<TenantDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? new(Outcome.NotFound) : new(Outcome.Ok, Scope(ToDto(t)));
    }

    public async Task<Result<TenantDto>> CreateAsync(CreateTenantRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.FullName)) return new(Outcome.Invalid, Error: "El nombre es obligatorio.");
        var email = Tenant.NormalizeEmail(req.Email); var phone = Tenant.NormalizePhone(req.Phone);
        if (Validate(req.FullName, email, phone, req.Notes) is { } error) return new(Outcome.Invalid, Error: error);

        var now = clock.UtcNow;
        var t = new Tenant { FullName = req.FullName.Trim(), Email = email, Phone = phone, Notes = Blank(req.Notes), CreatedAt = now, UpdatedAt = now };
        db.Tenants.Add(t);
        audit.Write("tenant.created", nameof(Tenant), t.Id); // no personal data in audit metadata
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, ToDto(t));
    }

    public async Task<Result<TenantDto>> UpdateAsync(Guid id, UpdateTenantRequest req, CancellationToken ct)
    {
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return new(Outcome.NotFound);

        var email = req.Email is null ? t.Email : Tenant.NormalizeEmail(req.Email);
        var phone = req.Phone is null ? t.Phone : Tenant.NormalizePhone(req.Phone);
        var fullName = req.FullName?.Trim() ?? t.FullName;
        var notes = req.Notes is null ? t.Notes : Blank(req.Notes);
        if (Validate(fullName, email, phone, notes) is { } error) return new(Outcome.Invalid, Error: error);
        if (req.Status == TenantStatus.Archived && t.Status != TenantStatus.Archived
            && await db.Leases.AnyAsync(l => l.TenantId == t.Id && l.Status == LeaseStatus.Active, ct))
            return new(Outcome.Conflict, Error: "El inquilino tiene contratos vigentes. Termínalos o cancélalos antes de archivarlo.");

        var changed = new List<string>();
        if (fullName != t.FullName) changed.Add("fullName");
        if (email != t.Email) changed.Add("email");
        if (phone != t.Phone) changed.Add("phone");
        if (notes != t.Notes) changed.Add("notes");
        (t.FullName, t.Email, t.Phone, t.Notes) = (fullName, email, phone, notes);

        if (changed.Count > 0) audit.Write("tenant.updated", nameof(Tenant), t.Id, metadata: new { fields = changed });
        if (req.Status is { } status && status != t.Status)
        {
            t.Status = status;
            audit.Write(status == TenantStatus.Archived ? "tenant.archived" : "tenant.restored", nameof(Tenant), t.Id, req.Reason);
        }
        t.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, ToDto(t));
    }

    private static TenantDto ToDto(Tenant t) => new(t.Id, t.FullName, t.Email, t.Phone, t.Notes, t.Status, t.CreatedAt);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Validate(string fullName, string? email, string? phone, string? notes)
    {
        if (fullName.Trim().Length is 0 or > 200) return "El nombre debe tener entre 1 y 200 caracteres.";
        if (email is not null && !Tenant.IsValidEmail(email)) return "El correo electrónico no es válido.";
        if (phone is not null && !Tenant.IsValidPhone(phone)) return "El teléfono debe tener al menos 7 dígitos y solo números, espacios y + - ( ).";
        if (notes is not null && notes.Length > 2000) return "Las notas no pueden superar 2000 caracteres.";
        return null;
    }
}
