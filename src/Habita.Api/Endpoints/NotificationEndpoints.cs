using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Habita.Api.Auth;
using Habita.Application.Abstractions;
using Habita.Application.Notifications;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Habita.Api.Endpoints;

public static class NotificationEndpoints
{
    /// <summary>
    /// Each person's own notifications, for every role including the tenant portal login. No route takes a user id: the list is always the signed-in
    /// person's, so there is nothing to change to read someone else's.
    /// </summary>
    public static void MapNotificationEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/notifications").RequireAuthorization(Policies.AnyMember);
        g.MapGet("", async (NotificationService svc, bool? unreadOnly, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(unreadOnly ?? false, page ?? 1, pageSize ?? 20, ct)));
        g.MapGet("/unread-count", async (NotificationService svc, CancellationToken ct) => Results.Ok(new { unread = await svc.UnreadCountAsync(ct) }));
        g.MapPost("/{id:guid}/read", async (Guid id, NotificationService svc, CancellationToken ct) => (await svc.MarkReadAsync(id, ct)).ToResult());
        g.MapPost("/read-all", async (NotificationService svc, CancellationToken ct) => Results.Ok(new { marked = await svc.MarkAllReadAsync(ct) }));
        g.MapGet("/stream", Stream);

        g.MapGet("/preferences", async (NotificationService svc, CancellationToken ct) => Results.Ok(await svc.GetPreferencesAsync(ct)));
        g.MapPut("/preferences", async (UpdatePreferencesRequest req, NotificationService svc, CancellationToken ct) => Results.Ok(await svc.UpdatePreferencesAsync(req, ct)));

        g.MapGet("/push/config", (NotificationService svc) => Results.Ok(svc.PushConfig()));
        g.MapPost("/push/subscriptions", async (SubscribePushRequest req, NotificationService svc, CancellationToken ct) =>
            (await svc.SubscribeAsync(req, ct)).ToResult(_ => Results.NoContent())).RequireRateLimiting(Habita.Api.Security.RateLimiting.Push);
        g.MapPost("/push/unsubscribe", async (UnsubscribePushRequest req, NotificationService svc, CancellationToken ct) =>
            await svc.UnsubscribeAsync(req, ct) ? Results.NoContent() : Results.NotFound()).RequireRateLimiting(Habita.Api.Security.RateLimiting.Push);
    }

    // At most this many open streams per person (a few tabs and devices), so one login cannot hold the server's connections.
    private const int MaxStreamsPerUser = 5;
    private static readonly ConcurrentDictionary<Guid, int> OpenStreams = new();

    /// <summary>
    /// Server-Sent Events. Sends notifications as they are created, checking the database every few seconds (the worker and other API instances
    /// create notifications too, so the database is the one place everyone shares). The stream ends after a few minutes and the browser reconnects
    /// on its own, which re-checks the session: a person removed from the organization stops receiving within minutes.
    /// </summary>
    private static async Task Stream(HttpContext ctx, NotificationService svc, ICurrentUser user, IClock clock, IConfiguration config, IOptions<JsonOptions> json, CancellationToken ct)
    {
        var me = user.UserId!.Value;
        if (OpenStreams.AddOrUpdate(me, 1, (_, n) => n + 1) > MaxStreamsPerUser)
        {
            OpenStreams.AddOrUpdate(me, 0, (_, n) => Math.Max(0, n - 1));
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            var poll = TimeSpan.FromSeconds(Math.Max(0.2, config.GetValue("Notifications:StreamPollSeconds", 3.0)));
            var maxLife = TimeSpan.FromSeconds(config.GetValue("Notifications:StreamMaxSeconds", 300.0));
            var res = ctx.Response;
            res.ContentType = "text/event-stream";
            res.Headers.CacheControl = "no-store";
            res.Headers["X-Accel-Buffering"] = "no"; // do not let a proxy hold the events back
            await res.WriteAsync("retry: 3000\n\n", ct);
            await res.Body.FlushAsync(ct);

            var life = Stopwatch.StartNew(); var beat = Stopwatch.StartNew();
            var cursor = clock.UtcNow.AddSeconds(-60); // a minute of history covers what was created between loading the list and opening the stream
            var sent = new Dictionary<Guid, DateTimeOffset>();
            while (!ct.IsCancellationRequested && life.Elapsed < maxLife)
            {
                // The 30 s overlap is deliberate: two transactions can commit in the opposite order of their timestamps, and the ids already sent make the overlap harmless.
                foreach (var n in await svc.CreatedSinceAsync(cursor.AddSeconds(-30), ct))
                {
                    if (sent.ContainsKey(n.Id)) continue;
                    await res.WriteAsync($"id: {n.Id}\nevent: notification\ndata: {JsonSerializer.Serialize(n, json.Value.SerializerOptions)}\n\n", ct);
                    sent[n.Id] = n.CreatedAt;
                    if (n.CreatedAt > cursor) cursor = n.CreatedAt;
                    beat.Restart();
                }
                foreach (var old in sent.Where(kv => kv.Value < cursor.AddMinutes(-2)).Select(kv => kv.Key).ToList()) sent.Remove(old);
                if (beat.Elapsed > TimeSpan.FromSeconds(15)) { await res.WriteAsync(": ping\n\n", ct); beat.Restart(); }
                await res.Body.FlushAsync(ct);
                await Task.Delay(poll, ct);
            }
        }
        catch (OperationCanceledException) { /* the person closed the tab */ }
        finally { OpenStreams.AddOrUpdate(me, 0, (_, n) => Math.Max(0, n - 1)); }
    }
}
