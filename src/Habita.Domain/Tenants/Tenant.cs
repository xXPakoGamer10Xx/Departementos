using Habita.Domain.Common;

namespace Habita.Domain.Tenants;

public enum TenantStatus { Active, Archived }

public class Tenant : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static string? NormalizeEmail(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    public static string? NormalizePhone(string? phone) => string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();

    public static bool IsValidEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 3 && email.IndexOf('.', at) > at + 1
               && !email.Contains(' ') && email.Length <= 256;
    }

    /// <summary>At least 7 digits and only digits, spaces and + - ( ) . characters.</summary>
    public static bool IsValidPhone(string phone) =>
        phone.Length <= 30 && phone.All(c => char.IsDigit(c) || " +-().".Contains(c)) && phone.Count(char.IsDigit) >= 7;
}
