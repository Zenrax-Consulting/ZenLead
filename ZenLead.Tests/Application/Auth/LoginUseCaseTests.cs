using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;

namespace ZenLead.Tests.Application.Auth;

public class LoginUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_ValidCredentials_ReturnsTokens()
    {
        var identity = new FakeIdentityService { RegisteredEmails = { "a@acme.com" } };
        var sut = new LoginUseCase(identity, new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        var result = await sut.ExecuteAsync(new LoginRequest("a@acme.com", "whatever"));

        Assert.NotNull(result);
        Assert.Equal("fake-refresh-token", result!.RefreshToken);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownEmail_ReturnsNull()
    {
        var sut = new LoginUseCase(new FakeIdentityService(), new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

        var result = await sut.ExecuteAsync(new LoginRequest("nobody@acme.com", "whatever"));

        Assert.Null(result);
    }
}
