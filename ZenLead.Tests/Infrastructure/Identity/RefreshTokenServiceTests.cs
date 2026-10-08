using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Identity;

/// <summary>
/// Uses a real (file-backed) SQLite database rather than the EF in-memory provider: rotation relies on a
/// conditional ExecuteUpdate and on foreign keys, neither of which the in-memory provider supports.
/// </summary>
public sealed class RefreshTokenServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zenlead-tests-{Guid.NewGuid():N}.db");

    private ZenLeadDbContext NewDb()
        => new(new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlite($"Data Source={_path}").Options, new FakeCurrentWorkspace());

    private async Task<Guid> SeedUserAsync()
    {
        using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTime.UtcNow };
        var user = new AppUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@acme.com", WorkspaceId = workspace.Id };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task ValidateAndRotateAsync_ValidToken_RotatesAndRevokesOld()
    {
        var userId = await SeedUserAsync();
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        var raw = await sut.IssueAsync(userId);

        var result = await sut.ValidateAndRotateAsync(raw);

        Assert.True(result.Succeeded);
        Assert.Equal(userId, result.UserId);
        Assert.NotEqual(raw, result.NewRawToken);

        var reuse = await sut.ValidateAndRotateAsync(raw);
        Assert.False(reuse.Succeeded); // old token now revoked
    }

    [Fact]
    public async Task ValidateAndRotateAsync_UnknownToken_Fails()
    {
        await SeedUserAsync();
        using var db = NewDb();

        var result = await new RefreshTokenService(db).ValidateAndRotateAsync("never-issued");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidateAndRotateAsync_ExpiredToken_Fails()
    {
        var userId = await SeedUserAsync();
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        var raw = await sut.IssueAsync(userId);

        var token = await db.RefreshTokens.FirstAsync();
        token.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        var result = await sut.ValidateAndRotateAsync(raw);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidateAndRotateAsync_ConcurrentUseOfSameToken_ExactlyOneSucceeds()
    {
        var userId = await SeedUserAsync();
        string raw;
        using (var db = NewDb()) raw = await new RefreshTokenService(db).IssueAsync(userId);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                using var db = NewDb();
                return await new RefreshTokenService(db).ValidateAndRotateAsync(raw);
            }));

            Assert.Single(results, r => r.Succeeded);
            raw = results.Single(r => r.Succeeded).NewRawToken!; // continue the chain with the rotated token
        }
    }

    [Fact]
    public async Task IssueAsync_RemovesThatUsersExpiredTokens_AndKeepsOthers()
    {
        var userId = await SeedUserAsync();
        var otherUserId = await SeedUserAsync();
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        await sut.IssueAsync(userId);
        await sut.IssueAsync(otherUserId);
        foreach (var t in db.RefreshTokens) t.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        await sut.IssueAsync(userId); // triggers cleanup for userId only

        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.UserId == userId));
        Assert.Equal(1, await db.RefreshTokens.CountAsync(t => t.UserId == otherUserId)); // untouched
    }

    [Fact]
    public async Task IssueAsync_UnknownUser_ViolatesForeignKey()
    {
        await SeedUserAsync();
        using var db = NewDb();

        await Assert.ThrowsAsync<DbUpdateException>(() => new RefreshTokenService(db).IssueAsync(Guid.NewGuid()));
    }
}
