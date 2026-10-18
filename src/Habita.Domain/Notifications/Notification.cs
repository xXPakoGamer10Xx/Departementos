using Habita.Domain.Common;

namespace Habita.Domain.Notifications;

public enum NotificationType
{
    ClaimSubmitted, ClaimConfirmed, ClaimRejected, IssueReported, CashCollected,
    RentDueSoon, ChargeOverdue, LeaseExpiring, PromiseDue, PromiseBroken,
}

/// <summary>
/// One message for one person in one organization. It is created in the same transaction as the event it reports (or by the daily reminder run),
/// and delivered by email and push later. The in-app list reads this same row.
/// </summary>
public class Notification : IOrganizationOwned
{
    public const int MaxDeliveryAttempts = 5;
    /// <summary>An email or push older than this is no longer worth sending (the worker was down); the in-app entry stays.</summary>
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromHours(48);

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Where it leads inside the app: always a path starting with "/", never an absolute URL.</summary>
    public string Url { get; set; } = "/";
    /// <summary>Makes a reminder idempotent: the same key for the same person is stored once, however many times the run repeats.</summary>
    public string? DedupKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset? EmailSentAt { get; set; }
    public DateTimeOffset? PushSentAt { get; set; }
    /// <summary>Set when no channel has anything left to do: sent, switched off, not applicable, or given up.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }

    public static string? Validate(string title, string body, string url)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return "El título es obligatorio y no puede superar 200 caracteres.";
        if (body.Length > 500) return "El texto no puede superar 500 caracteres.";
        if (!url.StartsWith('/') || url.StartsWith("//") || url.Contains('\\') || url.Length > 200) return "El enlace debe ser una ruta interna.";
        return null;
    }

    /// <summary>Backoff before the next try after a failed delivery: 1, 5, 15 and 60 minutes.</summary>
    public static TimeSpan Backoff(int attempts) => attempts switch { <= 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5), 3 => TimeSpan.FromMinutes(15), _ => TimeSpan.FromHours(1) };
}

/// <summary>A person's choices about the copies sent outside the app. The in-app list is always on. Absent row = both on.</summary>
public class NotificationPreference
{
    public Guid UserId { get; set; }
    public bool EmailEnabled { get; set; } = true;
    public bool PushEnabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A browser/device that agreed to receive push messages for a person. Not tied to an organization: the device belongs to the person.</summary>
public class PushSubscription
{
    public const int MaxPerUser = 10;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The server will POST to this address, so it must be a real push service and nothing else: https, port 443, no credentials, and a host
    /// that is one of the allowed push services (or a subdomain of one). Otherwise a person could aim the server at an internal address.
    /// </summary>
    public static bool IsAllowedEndpoint(string? endpoint, IEnumerable<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 1000 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return allowedHosts.Any(a => host == a || host.EndsWith("." + a, StringComparison.Ordinal));
    }

    public static readonly string[] DefaultAllowedHosts =
        ["fcm.googleapis.com", "updates.push.services.mozilla.com", "push.services.mozilla.com", "push.apple.com", "notify.windows.com"];

    public static string? ValidateKeys(string? p256dh, string? auth) =>
        string.IsNullOrWhiteSpace(p256dh) || p256dh.Length > 200 || string.IsNullOrWhiteSpace(auth) || auth.Length > 100 ? "Las llaves de la suscripción no son válidas." : null;
}

/// <summary>How a person's name appears in a message: the name first and then the surnames, exactly as the record holds them.</summary>
public static class PersonName
{
    /// <summary>Collapses stray whitespace. The order is never changed: "Ana María Pérez López" stays "Ana María Pérez López", never "Pérez López, Ana María".</summary>
    public static string Display(string? fullName) =>
        string.Join(' ', (fullName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
