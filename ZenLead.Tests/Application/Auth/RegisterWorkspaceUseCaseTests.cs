using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Tests.Application.Auth;

public class RegisterWorkspaceUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesWorkspaceAndReturnsTokens()
    {
        var sut = new RegisterWorkspaceUseCase(
            new FakeWorkspaceRepository(), new FakeIdentityService(),
            new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        var result = await sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123", "Alice"));

        Assert.NotNull(result.AccessToken);
        Assert.Equal("fake-refresh-token", result.RefreshToken);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateEmail_Throws()
    {
        var identity = new FakeIdentityService { RegisteredEmails = { "a@acme.com" } };
        var sut = new RegisterWorkspaceUseCase(
            new FakeWorkspaceRepository(), identity,
            new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ExecuteAsync(new RegisterRequest("Acme", "a@acme.com", "Password123", "Alice")));
    }
}
