using System.Text.Json;
using Habita.Application.Abstractions;
using Habita.Domain.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPush;

namespace Habita.Infrastructure.Services;

/// <summary>
/// Sends Web Push messages (RFC 8030, VAPID). Off when no keys are configured. Redirects are not followed and the destination host must be on
/// the allow-list, so a subscription can never turn the server into a way to reach an internal address.
/// </summary>
public class WebPushSender : IPushSender
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly VapidDetails? _vapid;
    private readonly ILogger<WebPushSender> _log;

    public WebPushSender(IConfiguration config, ILogger<WebPushSender> log)
    {
        _log = log;
        var pub = config["Push:VapidPublicKey"]; var priv = config["Push:VapidPrivateKey"];
        if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrWhiteSpace(priv))
            _vapid = new VapidDetails(config["Push:Subject"] is { Length: > 0 } s ? s : "mailto:no-reply@habita.local", pub, priv);
        var extra = (config["Push:AllowedHosts"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(h => h.ToLowerInvariant());
        AllowedHosts = [.. Habita.Domain.Notifications.PushSubscription.DefaultAllowedHosts, .. extra];
    }

    public bool Enabled => _vapid is not null;
    public string? PublicKey => _vapid?.PublicKey;
    public IReadOnlyList<string> AllowedHosts { get; }

    public async Task<PushOutcome> SendAsync(PushTarget target, PushMessage message, CancellationToken ct)
    {
        if (_vapid is null) return PushOutcome.Failed;
        // Checked again here: the list may have changed since the device subscribed.
        if (!Habita.Domain.Notifications.PushSubscription.IsAllowedEndpoint(target.Endpoint, AllowedHosts)) return PushOutcome.Gone;
        try
        {
            var payload = JsonSerializer.Serialize(new { title = message.Title, body = message.Body, url = message.Url, tag = message.Tag });
            await new WebPushClient(Http).SendNotificationAsync(new WebPush.PushSubscription(target.Endpoint, target.P256dh, target.Auth), payload, _vapid, ct);
            return PushOutcome.Sent;
        }
        catch (WebPushException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            return PushOutcome.Gone; // the browser unsubscribed: forget this device
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "No se pudo enviar el aviso push.");
            return PushOutcome.Failed;
        }
    }
}

public class WebLinks(IConfiguration config) : IWebLinks
{
    public string Absolute(string path) => (config["Web:BaseUrl"] ?? "http://localhost:3000").TrimEnd('/') + path;
}

/// <summary>Who "the current user" is when no request exists (the worker). Nothing that needs a person can run as this.</summary>
public class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId => null;
}
