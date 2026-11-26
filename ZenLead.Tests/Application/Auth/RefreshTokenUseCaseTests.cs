using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Auth;

public class RefreshTokenUseCaseTests
{
    private class ScriptedRefreshTokenService(RefreshResult result) : IRefreshTokenService
    {
        public List<Guid> RevokedAllFor { get; } = [];
        public Task<string> IssueAsync(Guid userId, CancellationToken ct = default) => Task.FromResult("issued");
        public Task<RefreshResult> ValidateAndRotateAsync(string rawToken, CancellationToken ct = default) => Task.FromResult(result);
        public Task RevokeAllAsync(Guid userId, CancellationToken ct = default)
        {
            RevokedAllFor.Add(userId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ValidToken_ReturnsNewAccessAndRotatedRefreshToken()
    {
        var workspaceId = Guid.NewGuid();
        var identity = new FakeIdentityService();
        var user = identity.Add("a@acme.com", AppUserStatus.Active, workspaceId);
        var sut = new RefreshTokenUseCase(
            new ScriptedRefreshTokenService(new RefreshResult(true, user.Id, "rotated", null)),
            identity, new FakeJwtTokenGenerator());

        var result = await sut.ExecuteAsync(new RefreshRequest("old"));

        Assert.NotNull(result);
        Assert.Equal("rotated", result!.RefreshToken);
        Assert.Contains(user.Id.ToString(), result.AccessToken);
        Assert.Contains(workspaceId.ToString(), result.AccessToken);
    }

    [Fact]
    public async Task ExecuteAsync_SuperAdmin_KeepsRoleAndStaysWorkspaceless()
    {
        var identity = new FakeIdentityService();
        var admin = identity.Add("root@zenlead.test", AppUserStatus.Active, workspaceId: null, role: PlatformRole.SuperAdmin);
        var sut = new RefreshTokenUseCase(
            new ScriptedRefreshTokenService(new RefreshResult(true, admin.Id, "rotated", null)),
            identity, new FakeJwtTokenGenerator());

        var result = await sut.ExecuteAsync(new RefreshRequest("old"));

        Assert.Equal($"fake-token:{admin.Id}::root@zenlead.test:SuperAdmin", result!.AccessToken);
    }

    [Theory]
    [InlineData(AppUserStatus.Disabled)]
    [InlineData(AppUserStatus.Rejected)]
    [InlineData(AppUserStatus.PendingApproval)]
    public async Task ExecuteAsync_UserNoLongerActive_ReturnsNullAndRevokesAllTokens(AppUserStatus status)
    {
        var identity = new FakeIdentityService();
        var user = identity.Add("a@acme.com", status, Guid.NewGuid());
        var tokens = new ScriptedRefreshTokenService(new RefreshResult(true, user.Id, "rotated", null));
        var sut = new RefreshTokenUseCase(tokens, identity, new FakeJwtTokenGenerator());

        Assert.Null(await sut.ExecuteAsync(new RefreshRequest("old")));
        Assert.Equal([user.Id], tokens.RevokedAllFor);
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
            new FakeIdentityService(), new FakeJwtTokenGenerator());

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExecuteAsync(new RefreshRequest("x")));
    }
}
