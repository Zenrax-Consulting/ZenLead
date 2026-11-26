using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Infrastructure.Identity;

public class IdentityService(UserManager<AppUser> userManager) : IIdentityService
{
    private static readonly AppUser TimingDummy = new() { UserName = "timing@zenlead.invalid" };

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
        => await userManager.FindByEmailAsync(email) is not null;

    public async Task<UserLookup?> FindByEmailAsync(string email, CancellationToken ct = default)
        => ToLookup(await userManager.FindByEmailAsync(email));

    public async Task<UserLookup?> FindByIdAsync(Guid userId, CancellationToken ct = default)
        => ToLookup(await userManager.FindByIdAsync(userId.ToString()));

    public async Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, AppUserStatus status, CancellationToken ct = default)
    {
        var user = new AppUser { UserName = email, Email = email, WorkspaceId = workspaceId, DisplayName = displayName, Status = status };
        IdentityResult result;
        try
        {
            result = await userManager.CreateAsync(user, password);
        }
        catch (DbUpdateException)
        {
            // lost a race: the unique user-name index fired after Identity's own duplicate check
            if (await userManager.FindByEmailAsync(email) is not null) throw new EmailAlreadyRegisteredException();
            throw;
        }

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
    /// even with the right password, and the caller sees the same invalid result as for a wrong password.
    /// The status is reported only for a correct password, so a wrong one reveals nothing about the account.
    /// </remarks>
    public async Task<CredentialCheck> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null) return CredentialCheck.Invalid;
        if (await userManager.IsLockedOutAsync(user)) return new CredentialCheck(null, null, LockedOut: true);

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            await userManager.AccessFailedAsync(user);
            return CredentialCheck.Invalid;
        }

        if (await userManager.GetAccessFailedCountAsync(user) > 0)
            await userManager.ResetAccessFailedCountAsync(user);
        return new CredentialCheck(user.Id, user.Status, false);
    }

    public async Task SetStatusAsync(Guid userId, AppUserStatus status, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException($"User {userId} not found.");
        user.Status = status;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not update user status: " + string.Join("; ", result.Errors.Select(e => e.Code)));
    }

    public async Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? null : new UserClaimsData(user.WorkspaceId, user.Email!, user.PlatformRole, user.Status);
    }

    public Task<int> CountPendingAsync(Guid workspaceId, CancellationToken ct = default)
        => userManager.Users.CountAsync(u => u.WorkspaceId == workspaceId && u.Status == AppUserStatus.PendingApproval, ct);

    public void BurnPasswordHash() => userManager.PasswordHasher.HashPassword(TimingDummy, "dummy-password-for-timing");

    private static UserLookup? ToLookup(AppUser? user)
        => user is null ? null : new UserLookup(user.Id, user.Status, user.WorkspaceId, user.DisplayName, user.Email!);
}
