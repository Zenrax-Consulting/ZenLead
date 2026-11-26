using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

/// <summary>What goes into an access token. <see cref="WorkspaceId"/> is null only for the super admin.</summary>
public record UserClaimsData(Guid? WorkspaceId, string Email, PlatformRole Role, AppUserStatus Status);

public record UserLookup(Guid Id, AppUserStatus Status, Guid? WorkspaceId, string DisplayName, string Email);

/// <summary><see cref="Status"/> is set only when the password was correct, so a wrong password reveals nothing about the account.</summary>
public record CredentialCheck(Guid? UserId, AppUserStatus? Status, bool LockedOut)
{
    public bool Valid => UserId is not null;
    public static readonly CredentialCheck Invalid = new(null, null, false);
}

/// <summary>Outcome of <see cref="IIdentityService.ChangePasswordAsync"/> (F21).</summary>
public abstract record ChangePasswordResult
{
    public sealed record Success : ChangePasswordResult;
    public sealed record WrongCurrentPassword : ChangePasswordResult;
    public sealed record LockedOut : ChangePasswordResult;
    public sealed record PolicyFailed(IReadOnlyList<string> Errors) : ChangePasswordResult;
}

public interface IIdentityService
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<UserLookup?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<UserLookup?> FindByIdAsync(Guid userId, CancellationToken ct = default);

    /// <exception cref="EmailAlreadyRegisteredException">The address is taken (including a race on the same address).</exception>
    /// <exception cref="RegistrationFailedException">Identity rejected the password.</exception>
    Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, AppUserStatus status, CancellationToken ct = default);

    /// <summary>Lockout-aware. Unknown email, wrong password and a locked-out account all return an invalid check (callers can't tell which).</summary>
    Task<CredentialCheck> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);

    Task SetStatusAsync(Guid userId, AppUserStatus status, CancellationToken ct = default);
    Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Users of the workspace still awaiting approval (pending-request cap).</summary>
    Task<int> CountPendingAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>Runs Identity's password validators for the user; an empty list means the password is acceptable (F21).</summary>
    Task<IReadOnlyList<string>> ValidatePasswordAsync(Guid userId, string password, CancellationToken ct = default);

    /// <summary>Replaces the password (no current password needed), resets the failure count, clears lockout and rotates the security stamp.</summary>
    Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default);

    /// <summary>Lockout-aware: a wrong current password counts as a failed attempt, so the form can't be used to guess the password.</summary>
    Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Computes and discards a password hash so the "already registered" path costs the same as creating a user.</summary>
    void BurnPasswordHash();
}
