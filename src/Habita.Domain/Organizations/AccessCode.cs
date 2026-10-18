using System.Security.Cryptography;
using Habita.Domain.Common;

namespace Habita.Domain.Organizations;

/// <summary>
/// A short, single-use code an administrator hands to a person (for example over WhatsApp) so they can create an account
/// with a fixed role. A Tenant code is tied to one tenant record. Only the hash is stored: the code is shown once.
/// </summary>
public class AccessCode : IOrganizationOwned
{
    // Crockford-style alphabet: no 0/O/1/I/L, so a code read aloud or typed from a screenshot is not ambiguous. 32^10 = 50 bits.
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Length = 10;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string CodeHash { get; set; } = "";
    public Role Role { get; set; }
    public Guid? TenantId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;

    /// <summary>Roles a code can grant: a collector, or a tenant (which needs a tenant record).</summary>
    public static bool IsGrantable(Role role) => role is Role.Collector or Role.Tenant;

    public static string Generate()
    {
        var chars = new char[Length];
        for (var i = 0; i < Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Strips spaces and dashes and upper-cases, so "abcd-efgh-jk" and "ABCDEFGHJK" are the same code. Null when it cannot be a code.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = new string(input.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return s.Length == Length && s.All(c => Alphabet.Contains(c)) ? s : null;
    }

    public static string Format(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";

    public static string Hash(string normalizedCode) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("access-code:" + normalizedCode)));
}
