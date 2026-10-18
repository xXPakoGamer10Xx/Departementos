using Habita.Domain.Common;

namespace Habita.Domain.Properties;

public enum PropertyStatus { Active, Archived }

public class Property : IOrganizationOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Notes { get; set; }
    public PropertyStatus Status { get; set; } = PropertyStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
