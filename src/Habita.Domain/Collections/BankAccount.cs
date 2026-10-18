using Habita.Domain.Common;

namespace Habita.Domain.Collections;

public enum BankAccountStatus { Active, Archived }

/// <summary>
/// A bank account where tenants pay by transfer. Without a property it is a general account of the organization (one of them is the default);
/// with a property it is the account for that property's units. Accounts are archived, never deleted.
/// </summary>
public class BankAccount : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? PropertyId { get; set; }
    public string HolderName { get; set; } = "";
    /// <summary>Detected from the CLABE prefix; "Banco no identificado" when the prefix is not in the catalogue.</summary>
    public string BankName { get; set; } = "";
    public string Clabe { get; set; } = "";
    /// <summary>Only meaningful for general accounts (no property). At most one active general account is the default.</summary>
    public bool IsDefault { get; set; }
    public BankAccountStatus Status { get; set; } = BankAccountStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public const string UnknownBank = "Banco no identificado";

    public static string? ValidateHolder(string? holder) =>
        string.IsNullOrWhiteSpace(holder) || holder.Trim().Length > 200 ? "El titular es obligatorio y no puede superar 200 caracteres." : null;

    public static string? ValidateClabe(string? normalized) =>
        ClabeRules.IsValid(normalized) ? null : "La CLABE debe tener 18 dígitos y un dígito verificador correcto.";
}
