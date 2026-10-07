using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Tests.Application.Auth;

public class RefreshTokenUseCaseTests
{
    private class ScriptedRefreshTokenService(RefreshResult result) : IRefreshTokenService
    {
        public Task<string> IssueAsync(Guid userId, CancellationToken ct = default) => Task.FromResult("issued");
        public Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default) => Task.FromResult(result);
    }

    private class UserlessIdentityService : IIdentityService
    {
        public Task<bool> EmailExistsAsync(string email, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Guid> CreateUserAsync(Guid workspaceId, string email, string password, string displayName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Guid?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<(Guid WorkspaceId, string Email)?> GetUserClaimsDataAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<(Guid, string)?>(null);
    }

    [Fact]
    public async Task ExecuteAsync_ValidToken_ReturnsNewAccessAndRotatedRefreshToken()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var identity = new FakeIdentityService { WorkspaceIdForUser = workspaceId };
        var sut = new RefreshTokenUseCase(
            new ScriptedRefreshTokenService(new RefreshResult(true, userId, "rotated", null)),
            identity, new FakeJwtTokenGenerator());

        var result = await sut.ExecuteAsync(new RefreshRequest("old"));

        Assert.NotNull(result);
        Assert.Equal("rotated", result!.RefreshToken);
        Assert.Contains(userId.ToString(), result.AccessToken);
        Assert.Contains(workspaceId.ToString(), result.AccessToken);
    }

    [Fact]
    public async Task ExecuteAsync_RejectedToken_ReturnsNull()
    {
        var sut = new RefreshTokenUseCase(
            new ScriptedRefreshTokenService(new RefreshResult(false, Guid.Empty, null, "Token revoked.")),
            new FakeIdentityService(), new FakeJwtTokenGenerator());

        Assert.Null(await sut.ExecuteAsync(new RefreshRequest("revoked")));
    }

    [Fact]
    public async Task ExecuteAsync_ValidTokenButUserGone_Throws()
    {
        var sut = new RefreshTokenUseCase(
            new ScriptedRefreshTokenService(new RefreshResult(true, Guid.NewGuid(), "rotated", null)),
            new UserlessIdentityService(), new FakeJwtTokenGenerator());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteAsync(new RefreshRequest("x")));
    }
}
