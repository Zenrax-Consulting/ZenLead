namespace ZenLead.Application.Abstractions;

public record RefreshResult(bool Succeeded, Guid UserId, string? NewRawToken, string? Error);

public interface IRefreshTokenService
{
    Task<string> IssueAsync(Guid userId, CancellationToken ct = default);
    Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default);

    /// <summary>Revokes every live refresh token of the user (rejection, disabling, password reset).</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken ct = default);
}
