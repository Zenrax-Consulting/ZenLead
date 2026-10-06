using Microsoft.AspNetCore.Identity;

namespace ZenLead.Infrastructure.Persistence;

public class AppUser : IdentityUser<Guid>
{
    public Guid WorkspaceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}
