using System.Text.Json;
using Habita.Application.Abstractions;
using Habita.Application.Properties;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Audit;

public record AuditEntryDto(Guid Id, string Action, string Label, string EntityType, Guid EntityId, Guid ActorUserId, string? ActorName,
    DateTimeOffset OccurredAt, string? Reason, JsonElement? Metadata);

/// <summary>Read-only view over the audit log. There is deliberately no way to change or delete an entry.</summary>
public class AuditService(IAppDbContext db, OrgCalendar calendar, IUserDirectory directory)
{
    public const int MaxPageSize = 100;

    public async Task<Paged<AuditEntryDto>> ListAsync(string? entityType, string? action, Guid? actorUserId, Guid? entityId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) { var t = entityType.Trim(); q = q.Where(a => a.EntityType == t); }
        if (!string.IsNullOrWhiteSpace(action)) { var term = action.Trim().ToLower(); q = q.Where(a => a.Action.ToLower().Contains(term)); }
        if (actorUserId is { } actor) q = q.Where(a => a.ActorUserId == actor);
        if (entityId is { } entity) q = q.Where(a => a.EntityId == entity);
        // Dates are the organization's local days: [start of from, start of the day after to) converted to UTC.
        if (from is { } f) { var start = await calendar.StartOfDayUtcAsync(f, ct); q = q.Where(a => a.OccurredAt >= start); }
        if (to is { } t2) { var end = await calendar.StartOfDayUtcAsync(t2.AddDays(1), ct); q = q.Where(a => a.OccurredAt < end); }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = await directory.NamesAsync(rows.Select(r => r.ActorUserId), ct);
        var items = rows.Select(r => new AuditEntryDto(r.Id, r.Action, AuditLabels.Label(r.Action), r.EntityType, r.EntityId, r.ActorUserId, names.GetValueOrDefault(r.ActorUserId),
            r.OccurredAt, r.Reason, string.IsNullOrEmpty(r.Metadata) ? null : JsonDocument.Parse(r.Metadata).RootElement.Clone())).ToList();
        return new(items, total, page, pageSize);
    }
}
