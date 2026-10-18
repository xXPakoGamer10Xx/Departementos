using System.Security.Cryptography;
using Habita.Domain.Common;
using Habita.Domain.Organizations;

namespace Habita.Domain.Collections;

/// <summary>
/// A short-lived single-use code the tenant shows (as a QR) to whoever collects the cash. It proves nothing by itself: staff must be signed in
/// to resolve it, and the payment it produces goes through the normal payment rules. Only the hash is stored; the code is shown once.
/// </summary>
public class CashToken : IOrganizationOwned
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);
    public const int MaxPerDay = 10;
    /// <summary>What the QR carries before the code, so a scanner can tell a Habita code from any other QR.</summary>
    public const string QrPrefix = "habita:cash:";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string CodeHash { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByUserId { get; set; }
    public Guid? PaymentId { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;

    // Same alphabet and length as access codes (no ambiguous characters, typeable when the camera fails).
    public static string Generate() => AccessCode.Generate();

    /// <summary>Accepts what the QR holds ("habita:cash:ABCD-EFGH-JK") or what a person types. Null when it cannot be a code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null) return null;
        var s = input.Trim();
        if (s.StartsWith(QrPrefix, StringComparison.OrdinalIgnoreCase)) s = s[QrPrefix.Length..];
        return AccessCode.Normalize(s);
    }

    public static string Format(string code) => AccessCode.Format(code);
    public static string QrPayload(string code) => QrPrefix + Format(code);

    public static string Hash(string normalizedCode) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("cash-token:" + normalizedCode)));
}
