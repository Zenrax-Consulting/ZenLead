using Microsoft.EntityFrameworkCore;
using ZenLead.Infrastructure.Identity;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Infrastructure.Identity;

public class RefreshTokenServiceTests
{
    private static ZenLeadDbContext NewDb() => new(
        new DbContextOptionsBuilder<ZenLeadDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task ValidateAndRotateAsync_ValidToken_RotatesAndRevokesOld()
    {
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        var userId = Guid.NewGuid();
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
        using var db = NewDb();
        var sut = new RefreshTokenService(db);

        var result = await sut.ValidateAndRotateAsync("never-issued");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ValidateAndRotateAsync_ExpiredToken_Fails()
    {
        using var db = NewDb();
        var sut = new RefreshTokenService(db);
        var userId = Guid.NewGuid();
        var raw = await sut.IssueAsync(userId);

        var token = await db.RefreshTokens.FirstAsync();
        token.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        var result = await sut.ValidateAndRotateAsync(raw);

        Assert.False(result.Succeeded);
    }
}
