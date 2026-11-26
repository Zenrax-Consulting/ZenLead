using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Security;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

/// <summary>Real file-backed SQLite with one connection per context, so the conditional UPDATE really races.</summary>
public sealed class PasswordResetRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zenlead-reset-{Guid.NewGuid():N}.db");
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    private ZenLeadDbContext NewDb()
        => new(new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlite($"Data Source={_path};Default Timeout=30").Options, new FakeCurrentWorkspace());

    private async Task<Guid> SeedUserAsync()
    {
        using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = $"Acme-{Guid.NewGuid()}", CreatedAt = Now };
        var user = new AppUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@acme.com", WorkspaceId = workspace.Id };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<string> AddAsync(Guid userId, DateTime createdAt)
    {
        var hash = TokenHasher.Hash(TokenHasher.NewUrlSafeToken());
        using var db = NewDb();
        await new PasswordResetRepository(db).AddAsync(new PasswordResetToken
        {
            Id = Guid.NewGuid(), UserId = userId, TokenHash = hash, CreatedAt = createdAt, ExpiresAt = createdAt.AddHours(1)
        });
        return hash;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task Concurrent_consumes_have_exactly_one_winner()
    {
        var userId = await SeedUserAsync();
        var hash = await AddAsync(userId, Now);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var db = NewDb();
            return await new PasswordResetRepository(db).TryConsumeAsync(hash, Now.AddMinutes(1));
        })));

        Assert.Single(results, r => r);
        using var check = NewDb();
        Assert.Equal(Now.AddMinutes(1), (await new PasswordResetRepository(check).FindByHashAsync(hash))!.UsedAt);
    }

    [Fact]
    public async Task Expired_token_cannot_be_consumed()
    {
        var userId = await SeedUserAsync();
        var hash = await AddAsync(userId, Now);

        using var db = NewDb();
        Assert.False(await new PasswordResetRepository(db).TryConsumeAsync(hash, Now.AddHours(1)));
    }

    [Fact]
    public async Task Invalidate_expires_only_unused_live_tokens_of_that_user()
    {
        var userId = await SeedUserAsync();
        var other = await SeedUserAsync();
        var used = await AddAsync(userId, Now);
        using (var db = NewDb()) await new PasswordResetRepository(db).TryConsumeAsync(used, Now);
        var live = await AddAsync(userId, Now.AddMinutes(1));
        var othersLive = await AddAsync(other, Now.AddMinutes(1));

        using (var db = NewDb()) await new PasswordResetRepository(db).InvalidateUnusedAsync(userId, Now.AddMinutes(2));

        using var check = NewDb();
        var repo = new PasswordResetRepository(check);
        Assert.Equal(Now.AddMinutes(2), (await repo.FindByHashAsync(live))!.ExpiresAt);
        Assert.Equal(Now.AddHours(1), (await repo.FindByHashAsync(used))!.ExpiresAt);
        Assert.Equal(Now.AddMinutes(61), (await repo.FindByHashAsync(othersLive))!.ExpiresAt);
    }

    [Fact]
    public async Task Count_and_housekeeping()
    {
        var userId = await SeedUserAsync();
        var ancient = await AddAsync(userId, Now.AddDays(-8));
        await AddAsync(userId, Now.AddMinutes(-90));
        await AddAsync(userId, Now.AddMinutes(-30));
        await AddAsync(userId, Now);                                  // this add deletes the 8-day-old row

        using var db = NewDb();
        var repo = new PasswordResetRepository(db);
        Assert.Null(await repo.FindByHashAsync(ancient));
        Assert.Equal(2, await repo.CountCreatedSinceAsync(userId, Now.AddHours(-1)));
        Assert.Equal(3, await db.PasswordResetTokens.CountAsync(t => t.UserId == userId));
    }
}
