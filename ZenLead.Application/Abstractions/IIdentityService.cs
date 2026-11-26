using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

/// <summary>What goes into an access token. <see cref="WorkspaceId"/> is null only for the super admin.</summary>
public record UserClaimsData(Guid? WorkspaceId, string Email, PlatformRole Role);

public interface IIdentityService
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default);

    /// <summary>Returns the user id, or null for unknown email, wrong password or a locked-out account (callers can't tell which).</summary>
    Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);
    Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default);
}
