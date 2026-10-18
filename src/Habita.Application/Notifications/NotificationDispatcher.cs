using Habita.Application.Abstractions;
using Habita.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Habita.Application.Notifications;

/// <summary>
/// Sends the email and push copies of notifications that were already saved. Rows are locked with SKIP LOCKED while they are being sent, so any
/// number of workers can run at once and none sends the same message twice. A failed channel is retried with a growing delay, a sent one never is.
/// </summary>
public class NotificationDispatcher(IAppDbContext db, INotificationStore store, IDbTransactions tx, IEmailSender email, IPushSender push, IUserDirectory users,
    IWebLinks links, IClock clock, ILogger<NotificationDispatcher> log)
{
    public const int BatchSize = 25;

    /// <summary>Delivers one batch for the current organization. Returns how many notifications were handled.</summary>
    public Task<int> DispatchBatchAsync(CancellationToken ct) => tx.RunAsync(async () =>
    {
        var now = clock.UtcNow;
        var batch = await store.LockPendingAsync(now, BatchSize, ct);
        if (batch.Count == 0) return 0;

        var userIds = batch.Select(n => n.UserId).Distinct().ToList();
        var emails = await users.EmailsAsync(userIds, ct);
        var prefs = await db.NotificationPreferences.Where(p => userIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);
        var subscriptions = push.Enabled ? await db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)).ToListAsync(ct) : [];

        foreach (var n in batch)
        {
            if (now - n.CreatedAt > Notification.DeliveryWindow) { n.DeliveredAt = now; continue; } // stale: the in-app entry is enough

            var pref = prefs.GetValueOrDefault(n.UserId);
            var failed = false;

            if (n.EmailSentAt is null && (pref?.EmailEnabled ?? true) && emails.TryGetValue(n.UserId, out var address))
            {
                try { await email.SendAsync(address, n.Title, EmailBody(n), ct); n.EmailSentAt = now; }
                catch (Exception e) when (e is not OperationCanceledException) { failed = true; log.LogWarning(e, "No se pudo enviar el correo de la notificación {Id}.", n.Id); }
            }

            if (n.PushSentAt is null && push.Enabled && (pref?.PushEnabled ?? true))
            {
                var mine = subscriptions.Where(s => s.UserId == n.UserId).ToList();
                var sent = 0; var failures = 0;
                foreach (var s in mine)
                {
                    // The text is only the title (which carries the name) and a generic line: a lock screen is not the place for amounts.
                    var outcome = await push.SendAsync(new(s.Endpoint, s.P256dh, s.Auth), new(n.Title, "Abre Habita para ver el detalle.", n.Url, n.Id.ToString()), ct);
                    if (outcome == PushOutcome.Sent) sent++;
                    else if (outcome == PushOutcome.Gone) { db.PushSubscriptions.Remove(s); subscriptions.Remove(s); }
                    else failures++;
                }
                if (sent > 0) n.PushSentAt = now;
                else if (failures > 0) failed = true;
            }

            if (!failed) { n.DeliveredAt = now; continue; }
            n.DeliveryAttempts++;
            if (n.DeliveryAttempts >= Notification.MaxDeliveryAttempts) { n.DeliveredAt = now; log.LogWarning("Se desistió de entregar la notificación {Id} tras {N} intentos.", n.Id, n.DeliveryAttempts); }
            else n.NextAttemptAt = now + Notification.Backoff(n.DeliveryAttempts);
        }
        await db.SaveChangesAsync(ct);
        return batch.Count;
    }, ct);

    private string EmailBody(Notification n) =>
        $"{n.Title}\n\n{n.Body}\n\nAbrir en Habita: {links.Absolute(n.Url)}\n\nPuedes elegir qué avisos recibes por correo en {links.Absolute("/notifications")}.\n";
}
