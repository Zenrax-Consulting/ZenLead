using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Auth;

public class FakeIdentityService : IIdentityService
{
    public List<string> RegisteredEmails { get; } = [];
    public Guid NextUserId { get; set; } = Guid.NewGuid();
    public Guid WorkspaceIdForUser { get; set; }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
        => Task.FromResult(RegisteredEmails.Contains(email));

    public Exception? ExceptionOnCreate { get; set; }

    public Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default)
    {
        if (ExceptionOnCreate is not null) throw ExceptionOnCreate;
        RegisteredEmails.Add(email);
        WorkspaceIdForUser = workspaceId;
        return Task.FromResult(NextUserId);
    }

    public Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
        => Task.FromResult<Guid?>(RegisteredEmails.Contains(email) ? NextUserId : null);

    public PlatformRole RoleForUser { get; set; } = PlatformRole.None;
    public bool UserHasNoWorkspace { get; set; }

    public Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult<UserClaimsData?>(new UserClaimsData(UserHasNoWorkspace ? null : WorkspaceIdForUser, "fake@example.com", RoleForUser));
}

public class FakeWorkspaceRepository : IWorkspaceRepository
{
    public List<Workspace> Created { get; } = [];

    /// <summary>Simulates losing the race to a simultaneous create (the unique index fires after NameExistsAsync said no).</summary>
    public bool ThrowNameTakenOnCreate { get; set; }

    public Task<Workspace> CreateAsync(string name, string? adminEmail = null, string timeZone = "UTC", CancellationToken ct = default)
    {
        if (ThrowNameTakenOnCreate) throw new WorkspaceNameTakenException();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, AdminEmail = adminEmail, TimeZone = timeZone, CreatedAt = DateTime.UtcNow };
        Created.Add(workspace);
        return Task.FromResult(workspace);
    }

    public Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Created.FirstOrDefault(w => w.Id == id));

    public Task<bool> UpdateTimeZoneAsync(Guid id, string timeZone, CancellationToken ct = default)
    {
        var workspace = Created.FirstOrDefault(w => w.Id == id);
        if (workspace is not null) workspace.TimeZone = timeZone;
        return Task.FromResult(workspace is not null);
    }

    public Task<bool> NameExistsAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Created.Any(w => string.Equals(w.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<WorkspaceAdminResponse>> ListWithUserCountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceAdminResponse>>(
            Created.Select(w => new WorkspaceAdminResponse(w.Id, w.Name, w.AdminEmail, w.TimeZone, 0, w.CreatedAt)).ToList());
}

public class FakeUnitOfWork : IUnitOfWork
{
    public bool Committed { get; private set; }
    public bool RolledBack { get; private set; }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        try
        {
            var result = await action();
            Committed = true;
            return result;
        }
        catch
        {
            RolledBack = true;
            throw;
        }
    }
}

public class FakeJwtTokenGenerator : IJwtTokenGenerator
{
    public string GenerateAccessToken(Guid userId, Guid? workspaceId, string email, PlatformRole role = PlatformRole.None)
        => role == PlatformRole.None ? $"fake-token:{userId}:{workspaceId}:{email}" : $"fake-token:{userId}:{workspaceId}:{email}:{role}";
}

public class FakeRefreshTokenService : IRefreshTokenService
{
    public Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult("fake-refresh-token");

    public Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
        => Task.FromResult(new RefreshResult(true, Guid.NewGuid(), "new-fake-refresh-token", null));
}
