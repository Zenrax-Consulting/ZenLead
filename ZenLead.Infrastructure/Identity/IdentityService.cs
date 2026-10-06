using Microsoft.AspNetCore.Identity;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Identity;

public class IdentityService(UserManager<AppUser> userManager) : IIdentityService
{
    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
        => await userManager.FindByEmailAsync(email) is not null;

    public async Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default)
    {
        var user = new AppUser { UserName = email, Email = email, WorkspaceId = workspaceId, DisplayName = displayName };
        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));

        return user.Id;
    }

    public async Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null) return null;
        return await userManager.CheckPasswordAsync(user, password) ? user.Id : null;
    }

    public async Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : (user.WorkspaceId, user.Email!);
    }
}
