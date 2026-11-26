using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Validation.Auth;

namespace ZenLead.Tests.Application.Auth;

public class PasswordValidatorTests
{
    [Theory]
    [InlineData("a@acme.com", true)]
    [InlineData("", false)]
    [InlineData("not-an-email", false)]
    public void Forgot_validates_the_email_format_only(string email, bool valid)
        => Assert.Equal(valid, new ForgotPasswordRequestValidator().Validate(new ForgotPasswordRequest(email)).IsValid);

    [Fact]
    public void Forgot_rejects_overlong_email()
        => Assert.False(new ForgotPasswordRequestValidator().Validate(new ForgotPasswordRequest(new string('a', 250) + "@acme.com")).IsValid);

    [Theory]
    [InlineData("tok", "Passw0rd!", true)]
    [InlineData("", "Passw0rd!", false)]
    [InlineData("tok", "password", false)]
    public void Reset_requires_a_token_and_a_strong_password(string token, string password, bool valid)
        => Assert.Equal(valid, new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest(token, password)).IsValid);

    [Fact]
    public void Reset_rejects_an_overlong_token()
        => Assert.False(new ResetPasswordRequestValidator().Validate(new ResetPasswordRequest(new string('t', 201), "Passw0rd!")).IsValid);

    [Theory]
    [InlineData("Old!Passw0rd", "N3w!Passw0rd", true)]
    [InlineData("", "N3w!Passw0rd", false)]
    [InlineData("Old!Passw0rd", "weak", false)]
    [InlineData("Same!Passw0rd", "Same!Passw0rd", false)]
    public void Change_requires_current_and_a_different_strong_new_password(string current, string next, bool valid)
        => Assert.Equal(valid, new ChangePasswordRequestValidator().Validate(new ChangePasswordRequest(current, next)).IsValid);
}
