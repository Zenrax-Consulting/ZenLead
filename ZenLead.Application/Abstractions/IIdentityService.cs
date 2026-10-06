namespace ZenLead.Application.Abstractions;

public interface IIdentityService
{
    Task<bool> EmailExistsAsync(string email, CancellationToken ct = default);
    Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default);
    Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default);
    Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default);
}
