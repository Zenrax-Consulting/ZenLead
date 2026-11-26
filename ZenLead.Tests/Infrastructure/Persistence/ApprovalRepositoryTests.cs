using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Security;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

/// <summary>Real file-backed SQLite with one connection per context, so the conditional UPDATE really races.</summary>
public sealed class ApprovalRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zenlead-approvals-{Guid.NewGuid():N}.db");
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    private ZenLeadDbContext NewDb()
        => new(new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlite($"Data Source={_path};Default Timeout=30").Options, new FakeCurrentWorkspace());

    private async Task<(Guid UserId, Guid WorkspaceId)> SeedUserAsync()
    {
        using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = $"Acme-{Guid.NewGuid()}", CreatedAt = Now };
        var user = new AppUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@acme.com", WorkspaceId = workspace.Id };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, workspace.Id);
    }

    private async Task<string> AddApprovalAsync(Guid userId, Guid workspaceId, DateTime createdAt)
    {
        var raw = TokenHasher.NewUrlSafeToken();
        using var db = NewDb();
        await new ApprovalRepository(db).AddAsync(new RegistrationApproval
        {
            Id = Guid.NewGuid(), UserId = userId, WorkspaceId = workspaceId, TokenHash = TokenHasher.Hash(raw),
            CreatedAt = createdAt, ExpiresAt = createdAt.AddDays(7)
        });
        return TokenHasher.Hash(raw);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void New_users_default_to_pending_approval()
        => Assert.Equal(AppUserStatus.PendingApproval, new AppUser().Status);

    [Fact]
    public async Task Concurrent_consumes_have_exactly_one_winner()
    {
        var (userId, workspaceId) = await SeedUserAsync();
        var hash = await AddApprovalAsync(userId, workspaceId, Now);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = NewDb();
            return await new ApprovalRepository(db).TryConsumeAsync(hash, Now.AddMinutes(1), ApprovalDecision.Approved);
        }));

        Assert.Equal(1, results.Count(r => r));
        using var check = NewDb();
        var row = await new ApprovalRepository(check).FindByHashAsync(hash);
        Assert.Equal(ApprovalDecision.Approved, row!.Decision);
        Assert.Equal(Now.AddMinutes(1), row.UsedAt);
    }

    [Fact]
    public async Task Consume_fails_once_expired()
    {
        var (userId, workspaceId) = await SeedUserAsync();
        var hash = await AddApprovalAsync(userId, workspaceId, Now);

        using var db = NewDb();
        Assert.False(await new ApprovalRepository(db).TryConsumeAsync(hash, Now.AddDays(7), ApprovalDecision.Approved));
    }

    [Fact]
    public async Task Invalidate_expires_only_unused_rows_and_last_created_is_reported()
    {
        var (userId, workspaceId) = await SeedUserAsync();
        var used = await AddApprovalAsync(userId, workspaceId, Now);
        using (var db = NewDb()) await new ApprovalRepository(db).TryConsumeAsync(used, Now, ApprovalDecision.Rejected);
        var open = await AddApprovalAsync(userId, workspaceId, Now.AddHours(2));

        using (var db = NewDb())
        {
            var repo = new ApprovalRepository(db);
            await repo.InvalidatePendingForUserAsync(userId, Now.AddHours(3));
            Assert.Equal(Now.AddHours(2), await repo.LastCreatedAtAsync(userId));
            Assert.Null(await repo.LastCreatedAtAsync(Guid.NewGuid()));
        }

        using var check = NewDb();
        var repo2 = new ApprovalRepository(check);
        Assert.Equal(Now.AddHours(3), (await repo2.FindByHashAsync(open))!.ExpiresAt);
        Assert.Equal(Now.AddDays(7), (await repo2.FindByHashAsync(used))!.ExpiresAt);
    }

    [Fact]
    public async Task RevokeAll_revokes_every_live_refresh_token_of_the_user_only()
    {
        var (userId, _) = await SeedUserAsync();
        var (otherId, _) = await SeedUserAsync();
        using (var db = NewDb())
        {
            var service = new RefreshTokenService(db);
            await service.IssueAsync(userId);
            await service.IssueAsync(userId);
            await service.IssueAsync(otherId);
            await service.RevokeAllAsync(userId);
        }

        using var check = NewDb();
        Assert.All(await check.RefreshTokens.Where(t => t.UserId == userId).ToListAsync(), t => Assert.NotNull(t.RevokedAt));
        Assert.All(await check.RefreshTokens.Where(t => t.UserId == otherId).ToListAsync(), t => Assert.Null(t.RevokedAt));
    }
}
