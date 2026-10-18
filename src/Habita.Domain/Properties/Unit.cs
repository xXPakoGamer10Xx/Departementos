using Habita.Domain.Common;

namespace Habita.Domain.Properties;

/// <summary>Occupied is derived from active leases (Hito 4) and is never set manually.</summary>
public enum UnitStatus { Available, Occupied, Maintenance, Archived }

public enum UnitType { Apartment, House, Room, Commercial, Other }

public class Unit : IOrganizationOwned
{
    public const int MaxBedrooms = 50;
    public const decimal MaxRent = 99_999_999.99m;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid PropertyId { get; set; }
    public string Code { get; set; } = "";
    public UnitType Type { get; set; } = UnitType.Apartment;
    public int? Bedrooms { get; set; }
    public decimal BaseRent { get; set; }
    public UnitStatus Status { get; set; } = UnitStatus.Available;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Codes are compared case-insensitively within a property, so they are stored trimmed and upper-case.</summary>
    public static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
}
