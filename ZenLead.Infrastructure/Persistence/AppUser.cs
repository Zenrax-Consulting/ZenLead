using Microsoft.AspNetCore.Identity;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

public class AppUser : IdentityUser<Guid>
{
    public Guid? WorkspaceId { get; set; }                   // null only for the super admin (enforced by a check constraint)
    public string DisplayName { get; set; } = string.Empty;
    public PlatformRole PlatformRole { get; set; } = PlatformRole.None;

    /// <summary>Defaults to pending on purpose: any code path that forgets to set it creates a user who cannot sign in (F20).</summary>
    public AppUserStatus Status { get; set; } = AppUserStatus.PendingApproval;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
