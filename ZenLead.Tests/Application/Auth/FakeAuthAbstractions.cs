using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Auth;

/// <summary>In-memory users with statuses. Passwords are stored as given (it's a fake).</summary>
public class FakeIdentityService : IIdentityService
{
    public class FakeUser
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public Guid? WorkspaceId { get; set; }
        public string Email { get; init; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public AppUserStatus Status { get; set; }
        public PlatformRole Role { get; set; } = PlatformRole.None;
        public int FailedAttempts { get; set; }
        public bool LockedOut { get; set; }
    }

    public Dictionary<Guid, FakeUser> Users { get; } = [];
    public Exception? ExceptionOnCreate { get; set; }
    public int BurnCount { get; private set; }

    public FakeUser Add(string email, AppUserStatus status, Guid? workspaceId = null, string password = "Passw0rd!", string displayName = "User",
        PlatformRole role = PlatformRole.None)
    {
        var user = new FakeUser { Email = email, Status = status, WorkspaceId = workspaceId, Password = password, DisplayName = displayName, Role = role };
        Users[user.Id] = user;
        return user;
    }

    private FakeUser? ByEmail(string email) => Users.Values.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    private static UserLookup? Lookup(FakeUser? u) => u is null ? null : new UserLookup(u.Id, u.Status, u.WorkspaceId, u.DisplayName, u.Email);

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default) => Task.FromResult(ByEmail(email) is not null);
    public Task<UserLookup?> FindByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult(Lookup(ByEmail(email)));
    public Task<UserLookup?> FindByIdAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Lookup(Users.GetValueOrDefault(userId)));

    public Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, AppUserStatus status, CancellationToken ct = default)
    {
        if (ExceptionOnCreate is not null) throw ExceptionOnCreate;
        if (ByEmail(email) is not null) throw new EmailAlreadyRegisteredException();
        return Task.FromResult(Add(email, status, workspaceId, password, displayName).Id);
    }

    public Task<CredentialCheck> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
        => Task.FromResult(ByEmail(email) is { } u && u.Password == password ? new CredentialCheck(u.Id, u.Status, false) : CredentialCheck.Invalid);

    public Task SetStatusAsync(Guid userId, AppUserStatus status, CancellationToken ct = default)
    {
        Users[userId].Status = status;
        return Task.CompletedTask;
    }

    public Task<UserClaimsData?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(Users.GetValueOrDefault(userId) is { } u ? new UserClaimsData(u.WorkspaceId, u.Email, u.Role, u.Status) : null);

    public Task<int> CountPendingAsync(Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult(Users.Values.Count(u => u.WorkspaceId == workspaceId && u.Status == AppUserStatus.PendingApproval));

    public void BurnPasswordHash() => BurnCount++;

    /// <summary>Mirrors Identity's options (length 8 + digit/lower/upper/symbol).</summary>
    public Task<IReadOnlyList<string>> ValidatePasswordAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (password.Length < 8) errors.Add("Passwords must be at least 8 characters.");
        if (!password.Any(char.IsDigit)) errors.Add("Passwords must have at least one digit ('0'-'9').");
        if (!password.Any(char.IsLower)) errors.Add("Passwords must have at least one lowercase ('a'-'z').");
        if (!password.Any(char.IsUpper)) errors.Add("Passwords must have at least one uppercase ('A'-'Z').");
        if (password.All(char.IsLetterOrDigit)) errors.Add("Passwords must have at least one non alphanumeric character.");
        return Task.FromResult<IReadOnlyList<string>>(errors);
    }

    public List<Guid> PasswordSetFor { get; } = [];

    public Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct = default)
    {
        var user = Users[userId];
        user.Password = newPassword;
        user.FailedAttempts = 0;
        user.LockedOut = false;
        PasswordSetFor.Add(userId);
        return Task.CompletedTask;
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = Users[userId];
        if (user.LockedOut) return new ChangePasswordResult.LockedOut();
        if (user.Password != currentPassword)
        {
            if (++user.FailedAttempts >= 5) user.LockedOut = true;
            return new ChangePasswordResult.WrongCurrentPassword();
        }
        var errors = await ValidatePasswordAsync(userId, newPassword, ct);
        if (errors.Count > 0) return new ChangePasswordResult.PolicyFailed(errors);
        user.Password = newPassword;
        user.FailedAttempts = 0;
        return new ChangePasswordResult.Success();
    }
}

/// <summary>In-memory reset tokens with the same atomic semantics as the EF repository.</summary>
public class FakePasswordResetRepository : IPasswordResetRepository
{
    public List<PasswordResetToken> Rows { get; } = [];

    public Task AddAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        lock (Rows) Rows.Add(token);
        return Task.CompletedTask;
    }

    public Task<int> CountCreatedSinceAsync(Guid userId, DateTime since, CancellationToken ct = default)
    {
        lock (Rows) return Task.FromResult(Rows.Count(t => t.UserId == userId && t.CreatedAt >= since));
    }

    public Task InvalidateUnusedAsync(Guid userId, DateTime now, CancellationToken ct = default)
    {
        lock (Rows)
            foreach (var t in Rows.Where(t => t.UserId == userId && t.UsedAt == null && t.ExpiresAt > now)) t.ExpiresAt = now;
        return Task.CompletedTask;
    }

    public Task<PasswordResetToken?> FindByHashAsync(string hash, CancellationToken ct = default)
    {
        lock (Rows)
            return Task.FromResult(Rows.FirstOrDefault(t => t.TokenHash == hash) is { } t
                ? new PasswordResetToken
                {
                    Id = t.Id, UserId = t.UserId, TokenHash = t.TokenHash, CreatedAt = t.CreatedAt,
                    ExpiresAt = t.ExpiresAt, UsedAt = t.UsedAt, RequestedIp = t.RequestedIp
                }
                : null);
    }

    public Task<bool> TryConsumeAsync(string hash, DateTime now, CancellationToken ct = default)
    {
        lock (Rows)
        {
            var row = Rows.FirstOrDefault(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now);
            if (row is null) return Task.FromResult(false);
            row.UsedAt = now;
            return Task.FromResult(true);
        }
    }
}

public class FakeApprovalRepository : IApprovalRepository
{
    public List<RegistrationApproval> Rows { get; } = [];

    public Task AddAsync(RegistrationApproval approval, CancellationToken ct = default)
    {
        Rows.Add(approval);
        return Task.CompletedTask;
    }

    public Task<RegistrationApproval?> FindByHashAsync(string tokenHash, CancellationToken ct = default)
        => Task.FromResult(Rows.FirstOrDefault(a => a.TokenHash == tokenHash) is { } a ? Copy(a) : null);

    public Task<bool> TryConsumeAsync(string tokenHash, DateTime now, ApprovalDecision decision, CancellationToken ct = default)
    {
        var row = Rows.FirstOrDefault(a => a.TokenHash == tokenHash && a.UsedAt == null && a.ExpiresAt > now);
        if (row is null) return Task.FromResult(false);
        row.UsedAt = now;
        row.Decision = decision;
        return Task.FromResult(true);
    }

    public Task InvalidatePendingForUserAsync(Guid userId, DateTime now, CancellationToken ct = default)
    {
        foreach (var row in Rows.Where(a => a.UserId == userId && a.UsedAt == null && a.ExpiresAt > now)) row.ExpiresAt = now;
        return Task.CompletedTask;
    }

    public Task<DateTime?> LastCreatedAtAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(Rows.Where(a => a.UserId == userId).Select(a => (DateTime?)a.CreatedAt).Max());

    // callers get a snapshot, like AsNoTracking
    private static RegistrationApproval Copy(RegistrationApproval a) => new()
    {
        Id = a.Id, UserId = a.UserId, WorkspaceId = a.WorkspaceId, TokenHash = a.TokenHash,
        CreatedAt = a.CreatedAt, ExpiresAt = a.ExpiresAt, UsedAt = a.UsedAt, Decision = a.Decision
    };
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

    public Task<IReadOnlyList<WorkspaceOption>> SearchByNameAsync(string? q, int max, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkspaceOption>>(Created
            .Where(w => string.IsNullOrWhiteSpace(q) || w.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(w => w.Name).Take(max).Select(w => new WorkspaceOption(w.Id, w.Name)).ToList());
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
    public List<Guid> RevokedAllFor { get; } = [];
    public List<Guid> IssuedFor { get; } = [];

    public Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        IssuedFor.Add(userId);
        return Task.FromResult("fake-refresh-token");
    }

    public Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default)
        => Task.FromResult(new RefreshResult(true, Guid.NewGuid(), "new-fake-refresh-token", null));

    public Task RevokeAllAsync(Guid userId, CancellationToken ct = default)
    {
        RevokedAllFor.Add(userId);
        return Task.CompletedTask;
    }
}
