namespace Habita.Domain.Organizations;

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Currency { get; set; } = "MXN";
    public string TimeZone { get; set; } = "America/Mexico_City";
    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
}
