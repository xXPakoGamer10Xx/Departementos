using Habita.Application.Abstractions;
using Habita.Application.Members;
using Habita.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Habita.Application.Notifications;

public record NotificationDto(Guid Id, NotificationType Type, string Title, string Body, string Url, DateTimeOffset CreatedAt, bool Read);
public record NotificationList(IReadOnlyList<NotificationDto> Items, int Total, int Page, int PageSize, int Unread);
public record PreferencesDto(bool EmailEnabled, bool PushEnabled);
public record UpdatePreferencesRequest(bool? EmailEnabled, bool? PushEnabled);
public record PushConfigDto(bool Enabled, string? PublicKey);
public record PushKeys(string? P256dh, string? Auth);
public record SubscribePushRequest(string? Endpoint, PushKeys? Keys);
public record UnsubscribePushRequest(string? Endpoint);

/// <summary>
/// What one signed-in person may do with their own notifications. Every query starts from the current user: there is no id that opens someone
/// else's list. The same holds for tenants (portal) and staff.
/// </summary>
public class NotificationService(IAppDbContext db, ICurrentUser user, IClock clock, IPushSender push)
{
    public const int MaxPageSize = 50;
    private Guid Me => user.UserId ?? throw new InvalidOperationException("No hay usuario autenticado.");

    public async Task<NotificationList> ListAsync(bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == Me);
        var q = unreadOnly ? mine.Where(n => n.ReadAt == null) : mine;
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(rows.Select(ToDto).ToList(), total, page, pageSize, await mine.CountAsync(n => n.ReadAt == null, ct));
    }

    public Task<int> UnreadCountAsync(CancellationToken ct) => db.Notifications.CountAsync(n => n.UserId == Me && n.ReadAt == null, ct);

    public async Task<Result<NotificationDto>> MarkReadAsync(Guid id, CancellationToken ct)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Me, ct);
        if (n is null) return new(Outcome.NotFound); // someone else's notification looks like a missing one
        if (n.ReadAt is null) { n.ReadAt = clock.UtcNow; await db.SaveChangesAsync(ct); }
        return new(Outcome.Ok, ToDto(n));
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct)
    {
        var now = clock.UtcNow; var me = Me;
        var unread = await db.Notifications.Where(n => n.UserId == me && n.ReadAt == null).ToListAsync(ct);
        foreach (var n in unread) n.ReadAt = now;
        await db.SaveChangesAsync(ct);
        return unread.Count;
    }

    /// <summary>
    /// What the live stream sends: notifications created at or after <paramref name="since"/>. The caller keeps the ids it already sent, because
    /// transactions can commit out of order and a short overlap is what makes sure none is skipped.
    /// </summary>
    public async Task<List<NotificationDto>> CreatedSinceAsync(DateTimeOffset since, CancellationToken ct) =>
        (await db.Notifications.AsNoTracking().Where(n => n.UserId == Me && n.CreatedAt >= since).OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Take(100).ToListAsync(ct))
            .Select(ToDto).ToList();

    // ---------- preferences ----------

    public async Task<PreferencesDto> GetPreferencesAsync(CancellationToken ct)
    {
        var p = await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == Me, ct);
        return p is null ? new(true, true) : new(p.EmailEnabled, p.PushEnabled);
    }

    public async Task<PreferencesDto> UpdatePreferencesAsync(UpdatePreferencesRequest req, CancellationToken ct)
    {
        var p = await db.NotificationPreferences.FirstOrDefaultAsync(x => x.UserId == Me, ct);
        var isNew = p is null;
        p ??= new NotificationPreference { UserId = Me };
        if (req.EmailEnabled is { } e) p.EmailEnabled = e;
        if (req.PushEnabled is { } pe) p.PushEnabled = pe;
        p.UpdatedAt = clock.UtcNow;
        if (isNew) db.NotificationPreferences.Add(p);
        await db.SaveChangesAsync(ct);
        return new(p.EmailEnabled, p.PushEnabled);
    }

    // ---------- push devices ----------

    public PushConfigDto PushConfig() => new(push.Enabled, push.Enabled ? push.PublicKey : null);

    public async Task<Result<bool>> SubscribeAsync(SubscribePushRequest req, CancellationToken ct)
    {
        if (!push.Enabled) return new(Outcome.Conflict, Error: "Los avisos en el dispositivo no están activados en este servidor.");
        if (!PushSubscription.IsAllowedEndpoint(req.Endpoint, push.AllowedHosts)) return new(Outcome.Invalid, Error: "La dirección de la suscripción no es válida.");
        if (PushSubscription.ValidateKeys(req.Keys?.P256dh, req.Keys?.Auth) is { } keyError) return new(Outcome.Invalid, Error: keyError);

        var me = Me;
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == req.Endpoint, ct);
        if (existing is null)
        {
            if (await db.PushSubscriptions.CountAsync(s => s.UserId == me, ct) >= PushSubscription.MaxPerUser)
                return new(Outcome.Conflict, Error: $"Ya tienes {PushSubscription.MaxPerUser} dispositivos. Quita alguno antes de añadir otro.");
            db.PushSubscriptions.Add(new PushSubscription { UserId = me, Endpoint = req.Endpoint!, P256dh = req.Keys!.P256dh!, Auth = req.Keys.Auth!, CreatedAt = clock.UtcNow });
        }
        else
        {
            // The same browser signed in as someone else: the device now belongs to whoever is using it, never to both.
            existing.UserId = me; existing.P256dh = req.Keys!.P256dh!; existing.Auth = req.Keys.Auth!;
        }
        await db.SaveChangesAsync(ct);
        return new(Outcome.Ok, true);
    }

    public async Task<bool> UnsubscribeAsync(UnsubscribePushRequest req, CancellationToken ct)
    {
        var me = Me;
        var s = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.UserId == me && x.Endpoint == req.Endpoint, ct);
        if (s is null) return false;
        db.PushSubscriptions.Remove(s);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static NotificationDto ToDto(Notification n) => new(n.Id, n.Type, n.Title, n.Body, n.Url, n.CreatedAt, n.ReadAt is not null);
}
