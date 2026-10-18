using Microsoft.AspNetCore.Identity;

namespace Habita.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";
}
