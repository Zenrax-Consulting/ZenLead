using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Auth;

public class LoginUseCaseTests
{
    private readonly FakeIdentityService _identity = new();
    private LoginUseCase Sut => new(_identity, new FakeJwtTokenGenerator(), new FakeRefreshTokenService());

    [Fact]
    public async Task Active_user_with_valid_credentials_gets_tokens()
    {
        _identity.Add("a@acme.com", AppUserStatus.Active, Guid.NewGuid(), password: "right");

        var result = await Sut.ExecuteAsync(new LoginRequest("a@acme.com", "right"));

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.Equal("fake-refresh-token", result.Tokens!.RefreshToken);
    }

    [Fact]
    public async Task Unknown_email_is_invalid_credentials()
    {
        var result = await Sut.ExecuteAsync(new LoginRequest("nobody@acme.com", "whatever"));

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Pending_user_with_correct_password_is_told_pending()
    {
        _identity.Add("p@acme.com", AppUserStatus.PendingApproval, Guid.NewGuid(), password: "right");

        var result = await Sut.ExecuteAsync(new LoginRequest("p@acme.com", "right"));

        Assert.Equal(LoginOutcome.PendingApproval, result.Outcome);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Pending_user_with_wrong_password_gets_no_hint()
    {
        _identity.Add("p@acme.com", AppUserStatus.PendingApproval, Guid.NewGuid(), password: "right");

        var result = await Sut.ExecuteAsync(new LoginRequest("p@acme.com", "wrong"));

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
    }

    [Theory]
    [InlineData(AppUserStatus.Rejected)]
    [InlineData(AppUserStatus.Disabled)]
    public async Task Rejected_or_disabled_user_is_not_approved(AppUserStatus status)
    {
        _identity.Add("r@acme.com", status, Guid.NewGuid(), password: "right");

        var result = await Sut.ExecuteAsync(new LoginRequest("r@acme.com", "right"));

        Assert.Equal(LoginOutcome.NotApproved, result.Outcome);
        Assert.Null(result.Tokens);
    }
}
