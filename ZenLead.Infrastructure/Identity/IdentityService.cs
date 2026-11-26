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
        {
            if (result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
                throw new EmailAlreadyRegisteredException();
            throw new RegistrationFailedException(result.Errors.Select(e => e.Description).ToList());
        }

        return user.Id;
    }

    /// <remarks>
    /// Counts failures towards Identity's lockout (see the Lockout options in Program.cs). A locked-out account is refused
    /// even with the right password, and the caller sees the same null as for a wrong password.
    /// </remarks>
    public async Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null) return null;
        if (await userManager.IsLockedOutAsync(user)) return null;

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            return null;
        }

        if (await userManager.GetAccessFailedCountAsync(user) > 0)
            await userManager.ResetAccessFailedCountAsync(user);
        return user.Id;
    }

    public async Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : new UserClaimsData(user.WorkspaceId, user.Email!, user.PlatformRole);
    }
}
