using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Validation.Auth;

namespace ZenLead.Tests.Application.Auth;

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _sut = new();

    [Theory]
    [InlineData("Passw0rd!", true)]
    [InlineData("password1", false)]   // no upper, no symbol — the case that used to orphan a workspace
    [InlineData("PASSWORD1!", false)]  // no lower
    [InlineData("Password!!", false)]  // no digit
    [InlineData("Password12", false)]  // no symbol
    [InlineData("Pa1!", false)]        // too short
    public void Password_MatchesIdentityRules(string password, bool expectedValid)
    {
        var result = _sut.Validate(new RegisterRequest("Acme", "a@acme.com", password, "Alice"));

        Assert.Equal(expectedValid, result.IsValid);
    }
}
