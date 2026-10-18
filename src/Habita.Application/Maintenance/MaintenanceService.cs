using System.Text.Json.Serialization;
using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Application.Properties;
using Habita.Domain.Common;
using Habita.Domain.Maintenance;
using Habita.Domain.Organizations;
using Habita.Domain.Properties;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Maintenance;

public record IssueEventDto(Guid Id, IssueEventType Type, string? ActorName, IssueStatus? FromStatus, IssueStatus? ToStatus, string? TargetName, string? Body, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TenantVisible = null);

/// <summary>Operational data only: location, text, status and people. Never rents, payments or tenant identity.</summary>
public record IssueDto(
    Guid Id, Guid PropertyId, string PropertyName, Guid? UnitId, string? UnitCode, string Title, string Description, IssuePriority Priority, IssueStatus Status,
    DateTimeOffset ReportedAt, Guid? AssignedToUserId, string? AssignedToName, DateTimeOffset? ResolvedAt, string? ResolvedByName, string? CreatedByName,
    DateTimeOffset UpdatedAt, IReadOnlyList<IssueEventDto>? Events,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReportedByTenant = null);

public record CreateIssueRequest(Guid PropertyId, Guid? UnitId, string Title, string? Description, IssuePriority? Priority, Guid? AssignedToUserId);
/// <summary>Absent field = unchanged. ClearUnit removes the unit (issue stays at property level).</summary>
public record UpdateIssueRequest(string? Title, string? Description, IssuePriority? Priority, Guid? UnitId, bool? ClearUnit);
public record ChangeIssueStatusRequest(IssueStatus Status, string? Note);
public record AssignIssueRequest(Guid? UserId);
/// <summary>VisibleToTenant only matters on issues a tenant reported; it defaults to false (internal).</summary>
public record CommentIssueRequest(string? Body, bool? VisibleToTenant = null);

public class MaintenanceService(IAppDbContext db, ICurrentOrganization org, ICurrentUser user, IAuditWriter audit, IClock clock, IUserDirectory directory)
{
    public const int MaxPageSize = 100;
    private bool IsAdmin => org.Role is Role.Owner or Role.Admin;
    private Guid Me => user.UserId!.Value;

    // ---------- queries ----------

    public async Task<Paged<IssueDto>> ListAsync(IssueStatus? status, bool activeOnly, IssuePriority? priority, Guid? propertyId, Guid? unitId, Guid? assignedTo,
        bool assignedToMe, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = from i in db.MaintenanceIssues
                join p in db.Properties on i.PropertyId equals p.Id
                join u in db.Units on i.UnitId equals u.Id into units
                from u in units.DefaultIfEmpty()
                select new { i, PropertyName = p.Name, UnitCode = u != null ? u.Code : null };
        if (status is { } s) q = q.Where(x => x.i.Status == s);
        if (activeOnly) q = q.Where(x => x.i.Status == IssueStatus.Open || x.i.Status == IssueStatus.InProgress || x.i.Status == IssueStatus.Waiting);
        if (priority is { } pr) q = q.Where(x => x.i.Priority == pr);
        if (propertyId is { } pid) q = q.Where(x => x.i.PropertyId == pid);
        if (unitId is { } uid) q = q.Where(x => x.i.UnitId == uid);
        if (assignedToMe) q = q.Where(x => x.i.AssignedToUserId == Me);
        else if (assignedTo is { } at) q = q.Where(x => x.i.AssignedToUserId == at);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(x => x.i.Title.ToLower().Contains(term) || x.PropertyName.ToLower().Contains(term) || (x.UnitCode != null && x.UnitCode.ToLower().Contains(term)));
        }
        var total = await q.CountAsync(ct);
        // Priority is stored as text; rank it explicitly so Urgent comes first.
        var rows = await q.OrderByDescending(x => x.i.Priority == IssuePriority.Urgent ? 3 : x.i.Priority == IssuePriority.High ? 2 : x.i.Priority == IssuePriority.Normal ? 1 : 0)
            .ThenByDescending(x => x.i.ReportedAt).ThenBy(x => x.i.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = await directory.NamesAsync(rows.SelectMany(r => new[] { r.i.AssignedToUserId, r.i.ResolvedByUserId, r.i.CreatedByUserId }).OfType<Guid>().Distinct(), ct);
        return new(rows.Select(r => ToDto(r.i, r.PropertyName, r.UnitCode, names, null)).ToList(), total, page, pageSize);
    }

    public async Task<Result<IssueDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var dto = await LoadAsync(id, ct);
        return dto is null ? new(Outcome.NotFound) : new(Outcome.Ok, dto);
    }

    // ---------- Owner/Admin: create, edit, assign ----------

    public async Task<Result<IssueDto>> CreateAsync(CreateIssueRequest req, CancellationToken ct)
    {
        var description = req.Description?.Trim() ?? "";
        if (MaintenanceIssue.ValidateText(req.Title, description) is { } error) return new(Outcome.Invalid, Error: error);
        if (req.Priority is { } pr && !Enum.IsDefined(pr)) return new(Outcome.Invalid, Error: "La prioridad no es válida.");
        if (await CheckLocationAsync(req.PropertyId, req.UnitId, ct) is { } locError) return new(locError.Outcome, Error: locError.Error);
        if (req.AssignedToUserId is { } assignee && await CheckAssigneeAsync(assignee, ct) is { } assigneeError) return new(Outcome.Invalid, Error: assigneeError);

        var now = clock.UtcNow;
        var issue = new MaintenanceIssue
        {
            PropertyId = req.PropertyId, UnitId = req.UnitId, Title = req.Title.Trim(), Description = description, Priority = req.Priority ?? IssuePriority.Normal,
            ReportedAt = now, UpdatedAt = now, CreatedByUserId = Me, AssignedToUserId = req.AssignedToUserId,
        };
        db.MaintenanceIssues.Add(issue);
        AddEvent(issue, IssueEventType.Created, now);
        if (req.AssignedToUserId is { } a) AddEvent(issue, IssueEventType.Assigned, now, target: a);
        audit.Write("issue.created", nameof(MaintenanceIssue), issue.Id, metadata: new { issue.PropertyId, issue.UnitId, issue.Priority, assigned = req.AssignedToUserId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(issue.Id, ct);
    }

    public async Task<Result<IssueDto>> UpdateAsync(Guid id, UpdateIssueRequest req, CancellationToken ct)
    {
        var issue = await db.MaintenanceIssues.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (issue is null) return new(Outcome.NotFound);
        if (issue.Status == IssueStatus.Closed) return new(Outcome.Conflict, Error: "Una incidencia cerrada no puede editarse.");

        var title = req.Title?.Trim() ?? issue.Title;
        var description = req.Description is null ? issue.Description : req.Description.Trim();
        if (MaintenanceIssue.ValidateText(title, description) is { } error) return new(Outcome.Invalid, Error: error);
        if (req.Priority is { } pr && !Enum.IsDefined(pr)) return new(Outcome.Invalid, Error: "La prioridad no es válida.");
        var unitId = req.ClearUnit == true ? null : req.UnitId ?? issue.UnitId;
        if (unitId != issue.UnitId && await CheckLocationAsync(issue.PropertyId, unitId, ct) is { } locError) return new(locError.Outcome, Error: locError.Error);

        var changes = new List<string>();
        if (title != issue.Title) changes.Add("título");
        if (description != issue.Description) changes.Add("descripción");
        if (unitId != issue.UnitId) changes.Add("unidad");
        if (req.Priority is { } newPriority && newPriority != issue.Priority) changes.Add($"prioridad {issue.Priority} → {newPriority}");
        if (changes.Count == 0) return await GetAsync(id, ct);

        (issue.Title, issue.Description, issue.UnitId) = (title, description, unitId);
        if (req.Priority is { } p2) issue.Priority = p2;
        var now = clock.UtcNow;
        issue.UpdatedAt = now;
        AddEvent(issue, IssueEventType.Updated, now, body: "Cambió: " + string.Join(", ", changes));
        audit.Write("issue.updated", nameof(MaintenanceIssue), issue.Id, metadata: new { fields = changes });
        return await SaveAsync(issue.Id, ct);
    }

    public async Task<Result<IssueDto>> AssignAsync(Guid id, AssignIssueRequest req, CancellationToken ct)
    {
        var issue = await db.MaintenanceIssues.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (issue is null) return new(Outcome.NotFound);
        if (issue.Status == IssueStatus.Closed) return new(Outcome.Conflict, Error: "Una incidencia cerrada no puede reasignarse.");
        if (req.UserId is { } uid && await CheckAssigneeAsync(uid, ct) is { } error) return new(Outcome.Invalid, Error: error);
        if (issue.AssignedToUserId == req.UserId) return await GetAsync(id, ct);

        var now = clock.UtcNow;
        issue.AssignedToUserId = req.UserId;
        issue.UpdatedAt = now;
        AddEvent(issue, IssueEventType.Assigned, now, target: req.UserId);
        audit.Write("issue.assigned", nameof(MaintenanceIssue), issue.Id, metadata: new { assignedTo = req.UserId });
        return await SaveAsync(issue.Id, ct);
    }

    // ---------- Owner/Admin, or Maintenance on issues assigned to them: status and comments ----------

    public async Task<Result<IssueDto>> ChangeStatusAsync(Guid id, ChangeIssueStatusRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(req.Status)) return new(Outcome.Invalid, Error: "El estado no es válido.");
        var note = req.Note?.Trim();
        if (note is { Length: > MaintenanceIssueEvent.MaxCommentLength }) return new(Outcome.Invalid, Error: "La nota es demasiado larga.");
        var issue = await db.MaintenanceIssues.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (issue is null) return new(Outcome.NotFound);
        if (Authorize(issue, closing: req.Status == IssueStatus.Closed, to: req.Status) is { } denied) return denied;

        var from = issue.Status;
        var now = clock.UtcNow;
        try { issue.ChangeStatus(req.Status, Me, now); }
        catch (DomainException e) { return new(Outcome.Conflict, Error: e.Message); }
        AddEvent(issue, IssueEventType.StatusChanged, now, from: from, to: req.Status, body: string.IsNullOrEmpty(note) ? null : note);
        audit.Write(req.Status == IssueStatus.Resolved ? "issue.resolved" : "issue.status_changed", nameof(MaintenanceIssue), issue.Id, metadata: new { from, to = req.Status });
        return await SaveAsync(issue.Id, ct);
    }

    public async Task<Result<IssueDto>> CommentAsync(Guid id, CommentIssueRequest req, CancellationToken ct)
    {
        if (MaintenanceIssueEvent.ValidateComment(req.Body) is { } error) return new(Outcome.Invalid, Error: error);
        var issue = await db.MaintenanceIssues.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (issue is null) return new(Outcome.NotFound);
        if (Authorize(issue, closing: false, to: null) is { } denied) return denied;
        if (issue.Status == IssueStatus.Closed) return new(Outcome.Conflict, Error: "Una incidencia cerrada no admite comentarios.");

        db.MaintenanceIssueEvents.Add(new MaintenanceIssueEvent { IssueId = id, Type = IssueEventType.Comment, ActorUserId = Me, Body = req.Body!.Trim(), TenantVisible = req.VisibleToTenant == true && issue.ReportedByTenantId is not null, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------- helpers ----------

    /// <summary>Owner/Admin may do anything valid. Maintenance may act only on issues assigned to them, and may not close.</summary>
    private Result<IssueDto>? Authorize(MaintenanceIssue issue, bool closing, IssueStatus? to)
    {
        if (IsAdmin) return null;
        if (org.Role != Role.Maintenance) return new(Outcome.Forbidden, Error: "No tienes permiso para actualizar incidencias.");
        if (issue.AssignedToUserId != Me) return new(Outcome.Forbidden, Error: "Solo puedes actualizar incidencias asignadas a ti.");
        if (closing || (to is { } t && !MaintenanceIssue.MaintenanceMayMove(t)))
            return new(Outcome.Forbidden, Error: "Solo un propietario o administrador puede cerrar o devolver a abierta una incidencia.");
        return null;
    }

    /// <summary>The property must be active and the unit (if any) must belong to it and not be archived.</summary>
    private async Task<Result<IssueDto>?> CheckLocationAsync(Guid propertyId, Guid? unitId, CancellationToken ct)
    {
        var property = await db.Properties.Where(p => p.Id == propertyId).Select(p => (PropertyStatus?)p.Status).FirstOrDefaultAsync(ct);
        if (property is null) return new(Outcome.Invalid, Error: "La propiedad no existe.");
        if (property == PropertyStatus.Archived) return new(Outcome.Conflict, Error: "La propiedad está archivada y no admite incidencias nuevas.");
        if (unitId is { } uid)
        {
            var unit = await db.Units.Where(u => u.Id == uid && u.PropertyId == propertyId).Select(u => (UnitStatus?)u.Status).FirstOrDefaultAsync(ct);
            if (unit is null) return new(Outcome.Invalid, Error: "La unidad no existe o no pertenece a esa propiedad.");
            if (unit == UnitStatus.Archived) return new(Outcome.Conflict, Error: "La unidad está archivada.");
        }
        return null;
    }

    private async Task<string?> CheckAssigneeAsync(Guid userId, CancellationToken ct)
    {
        var ok = await db.Memberships.AnyAsync(m => m.UserId == userId && m.Status == MembershipStatus.Active
            && (m.Role == Role.Maintenance || m.Role == Role.Admin || m.Role == Role.Owner), ct);
        return ok ? null : "Solo puede asignarse a un miembro activo con rol de mantenimiento, administrador o propietario.";
    }

    private void AddEvent(MaintenanceIssue issue, IssueEventType type, DateTimeOffset now, IssueStatus? from = null, IssueStatus? to = null, Guid? target = null, string? body = null) =>
        db.MaintenanceIssueEvents.Add(new MaintenanceIssueEvent { IssueId = issue.Id, Type = type, ActorUserId = Me, FromStatus = from, ToStatus = to, TargetUserId = target, Body = body, CreatedAt = now });

    private async Task<Result<IssueDto>> SaveAsync(Guid id, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return new(Outcome.Conflict, Error: "La incidencia cambió mientras la editabas. Recarga e inténtalo de nuevo.");
        }
        return await GetAsync(id, ct);
    }

    private async Task<IssueDto?> LoadAsync(Guid id, CancellationToken ct)
    {
        var row = await (from i in db.MaintenanceIssues.AsNoTracking()
                         join p in db.Properties on i.PropertyId equals p.Id
                         join u in db.Units on i.UnitId equals u.Id into units
                         from u in units.DefaultIfEmpty()
                         where i.Id == id
                         select new { i, PropertyName = p.Name, UnitCode = u != null ? u.Code : null }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var events = await db.MaintenanceIssueEvents.AsNoTracking().Where(e => e.IssueId == id).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct);
        var ids = events.SelectMany(e => new[] { e.ActorUserId, e.TargetUserId ?? Guid.Empty }).Concat([row.i.AssignedToUserId ?? Guid.Empty, row.i.ResolvedByUserId ?? Guid.Empty, row.i.CreatedByUserId])
            .Where(g => g != Guid.Empty).Distinct();
        var names = await directory.NamesAsync(ids, ct);
        var dtoEvents = events.Select(e => new IssueEventDto(e.Id, e.Type, row.i.ReportedByTenantId is not null && org.Role == Role.Maintenance && e.ActorUserId == row.i.CreatedByUserId ? "Inquilino" : names.GetValueOrDefault(e.ActorUserId), e.FromStatus, e.ToStatus,
            e.TargetUserId is { } t ? names.GetValueOrDefault(t) : null, e.Body, e.CreatedAt, org.Role == Role.Maintenance ? null : e.TenantVisible)).ToList();
        return ToDto(row.i, row.PropertyName, row.UnitCode, names, dtoEvents);
    }

    private IssueDto ToDto(MaintenanceIssue i, string propertyName, string? unitCode, Dictionary<Guid, string> names, IReadOnlyList<IssueEventDto>? events) =>
        new(i.Id, i.PropertyId, propertyName, i.UnitId, unitCode, i.Title, i.Description, i.Priority, i.Status, i.ReportedAt, i.AssignedToUserId,
            i.AssignedToUserId is { } a ? names.GetValueOrDefault(a) : null, i.ResolvedAt, i.ResolvedByUserId is { } r ? names.GetValueOrDefault(r) : null,
            // A technician learns where and what, not who lives there: a tenant's name is withheld from the Maintenance role.
            i.ReportedByTenantId is not null && org.Role == Role.Maintenance ? "Inquilino" : names.GetValueOrDefault(i.CreatedByUserId), i.UpdatedAt, events,
            org.Role == Role.Maintenance ? null : i.ReportedByTenantId is not null); // the technician does not need to know; it stays out of their payload
}
