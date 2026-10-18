namespace Habita.Domain.Common;

public interface IOrganizationOwned
{
    Guid OrganizationId { get; set; }
}
