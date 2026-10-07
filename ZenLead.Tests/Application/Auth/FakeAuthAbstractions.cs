using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;

namespace ZenLead.Tests.Application.Auth;

public class FakeIdentityService : IIdentityService
{
    public List<string> RegisteredEmails { get; } = [];
    public Guid NextUserId { get; set; } = Guid.NewGuid();
    public Guid WorkspaceIdForUser { get; set; }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
        => Task.FromResult(RegisteredEmails.Contains(email));

    public Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default)
    {
        RegisteredEmails.Add(email);
        WorkspaceIdForUser = workspaceId;
        return Task.FromResult(NextUserId);
    }

    public Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
        => Task.FromResult<Guid?>(RegisteredEmails.Contains(email) ? NextUserId : null);

    public Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<(Guid, string)?>((WorkspaceIdForUser, "fake@example.com"));
}

public class FakeWorkspaceRepository : IWorkspaceRepository
{
    public Task<Workspace> CreateAsync(string name, CancellationToken ct = default)
        => Task.FromResult(new Workspace { Id = Guid.NewGuid(), Name = name, CreatedAt = DateTime.UtcNow });
}

public class FakeJwtTokenGenerator : IJwtTokenGenerator
{
    public string GenerateAccessToken(Guid userId, Guid workspaceId, string email)
        => $"fake-token:{userId}:{workspaceId}:{email}";
}

public class FakeRefreshTokenService : IRefreshTokenService
{
    public Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult("fake-refresh-token");

    public Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
        => Task.FromResult(new RefreshResult(true, Guid.NewGuid(), "new-fake-refresh-token", null));
}
