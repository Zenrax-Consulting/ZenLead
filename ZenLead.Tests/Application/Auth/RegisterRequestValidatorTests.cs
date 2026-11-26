using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Validation.Auth;

namespace ZenLead.Tests.Application.Auth;

public class RegisterRequestValidatorTests
{
    private readonly RegisterRequestValidator _sut = new();

    [Theory]
    [InlineData("Passw0rd!", true)]
    [InlineData("password1", false)]   // no upper, no symbol
    [InlineData("PASSWORD1!", false)]  // no lower
    [InlineData("Password!!", false)]  // no digit
    [InlineData("Password12", false)]  // no symbol
    [InlineData("Pa1!", false)]        // too short
    public void Password_MatchesIdentityRules(string password, bool expectedValid)
    {
        var result = _sut.Validate(new RegisterRequest("a@acme.com", password, "Alice", Guid.NewGuid()));

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void WorkspaceId_is_required()
    {
        var result = _sut.Validate(new RegisterRequest("a@acme.com", "Passw0rd!", "Alice", Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RegisterRequest.WorkspaceId));
    }

    [Theory]
    [InlineData("", "Alice")]
    [InlineData("not-an-email", "Alice")]
    [InlineData("a@acme.com", "")]
    public void Email_and_display_name_are_validated(string email, string displayName)
        => Assert.False(_sut.Validate(new RegisterRequest(email, "Passw0rd!", displayName, Guid.NewGuid())).IsValid);
}
