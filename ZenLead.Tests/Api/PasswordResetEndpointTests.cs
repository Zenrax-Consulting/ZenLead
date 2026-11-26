using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Security;
using ZenLead.Application.Validation;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Api;

/// <summary>ApiFactory with the per-IP limits raised and no response padding (the padding itself is unit-tested).</summary>
public class PasswordResetApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:AuthPermitLimit", "10000");
        builder.UseSetting("RateLimits:ForgotPermitLimit", "10000");
        builder.UseSetting("PasswordReset:MinResponseMs", "0");
    }
}

/// <summary>Default forgot-password limit (10/hour/IP), everything else relaxed.</summary>
public class ForgotLimitApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:AuthPermitLimit", "10000");
        builder.UseSetting("PasswordReset:MinResponseMs", "0");
    }
}

/// <summary>F21 end to end: forgot → email → reset → login, and signed-in change-password.</summary>
public class PasswordResetEndpointTests(PasswordResetApiFactory factory) : IClassFixture<PasswordResetApiFactory>
{
    private HttpClient Anonymous => factory.ClientFor(null);
    private static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@acme.test";

    private async Task<(Guid UserId, string Email, string Password, string AccessToken)> UserAsync()
    {
        var email = NewEmail();
        var password = ApiFactory.NewPassword();
        var (_, userId, token) = await factory.CreateWorkspaceAndUserAsync($"Acme {Guid.NewGuid():N}", email, password);
        return (userId, email, password, token);
    }

    private async Task<(Guid UserId, string Email, string Password)> SuperAdminWithPasswordAsync()
    {
        var email = NewEmail("platform");
        var password = ApiFactory.NewPassword();
        var (userId, _) = await factory.CreateSuperAdminAsync(email);
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIdentityService>().SetPasswordAsync(userId, password);
        return (userId, email, password);
    }

    private Task<HttpResponseMessage> ForgotAsync(string email)
        => Anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email));

    private Task<HttpResponseMessage> ResetAsync(string token, string password)
        => Anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, password));

    private Task<HttpResponseMessage> LoginAsync(string email, string password)
        => Anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken)
        => Anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken));

    private Task<HttpResponseMessage> ChangeAsync(string accessToken, string current, string next)
        => factory.ClientFor(accessToken).PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(current, next));

    private async Task<AuthResponse> LoginOkAsync(string email, string password)
    {
        var response = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private OutboundEmail[] MailTo(string address) => factory.Email.Sent.Where(m => m.To.Address == address).ToArray();

    private async Task<string> RequestTokenAsync(string email)
    {
        Assert.Equal(HttpStatusCode.OK, (await ForgotAsync(email)).StatusCode);
        return MailTo(email).Last(m => m.Subject == "Reset your ZenLead password").TextBody.Split("token=")[1].Trim();
    }

    private static async Task<string> CodeOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    // ---- forgot ----

    [Fact]
    public async Task Forgot_returns_the_same_status_and_body_for_existing_unknown_and_pending_addresses()
    {
        var (_, email, _, _) = await UserAsync();
        var pendingEmail = NewEmail("pending");
        using (var scope = factory.Services.CreateScope())
        {
            var workspace = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().CreateAsync($"P {Guid.NewGuid():N}");
            await scope.ServiceProvider.GetRequiredService<IIdentityService>()
                .CreateUserAsync(workspace.Id, pendingEmail, ApiFactory.NewPassword(), "Pending", AppUserStatus.PendingApproval);
        }

        var existing = await ForgotAsync(email);
        var unknown = await ForgotAsync(NewEmail("nobody"));
        var pending = await ForgotAsync(pendingEmail);

        Assert.All([existing, unknown, pending], r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var body = await existing.Content.ReadAsStringAsync();
        Assert.Equal(body, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(body, await pending.Content.ReadAsStringAsync());
        Assert.Single(MailTo(email));
        Assert.Empty(MailTo(pendingEmail));
    }

    [Fact]
    public async Task Forgot_stores_only_the_hash_of_the_emailed_token()
    {
        var (userId, email, _, _) = await UserAsync();

        var raw = await RequestTokenAsync(email);

        var row = await factory.WithDbAsync(db => db.PasswordResetTokens.SingleAsync(t => t.UserId == userId));
        Assert.Equal(TokenHasher.Hash(raw), row.TokenHash);
        Assert.False(await factory.WithDbAsync(db => db.PasswordResetTokens.AnyAsync(t => t.TokenHash == raw)));
        Assert.StartsWith("https://app.zenlead.test/reset-password?token=", MailTo(email).Single().TextBody.Split("\n").Last().Split(": ")[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Forgot_validation_errors_only_for_malformed_input(string email)
        => Assert.Equal(HttpStatusCode.BadRequest, (await ForgotAsync(email)).StatusCode);

    [Fact]
    public async Task Forgot_works_for_the_super_admin()
    {
        var (_, email, _) = await SuperAdminWithPasswordAsync();

        var token = await RequestTokenAsync(email);
        var newPassword = ApiFactory.NewPassword();

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, newPassword)).StatusCode);
        await LoginOkAsync(email, newPassword);
    }

    // ---- reset ----

    [Fact]
    public async Task Reset_happy_path_changes_the_password_and_signs_out_every_session()
    {
        var (_, email, oldPassword, _) = await UserAsync();
        var session = await LoginOkAsync(email, oldPassword);
        var token = await RequestTokenAsync(email);
        var newPassword = ApiFactory.NewPassword();

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, newPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, oldPassword)).StatusCode);
        await LoginOkAsync(email, newPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.RefreshToken)).StatusCode);
        Assert.Equal("Your ZenLead password was changed", MailTo(email).Last().Subject);
    }

    [Fact]
    public async Task Replay_expired_tampered_and_empty_tokens_all_get_the_identical_invalid_body()
    {
        var (userId, email, _, _) = await UserAsync();
        var used = await RequestTokenAsync(email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(used, ApiFactory.NewPassword())).StatusCode);

        var expired = await RequestTokenAsync(email);
        await factory.WithDbAsync(db => db.PasswordResetTokens.Where(t => t.UserId == userId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));

        var responses = new[]
        {
            await ResetAsync(used, ApiFactory.NewPassword()),
            await ResetAsync(expired, ApiFactory.NewPassword()),
            await ResetAsync(expired[..^2] + "xx", ApiFactory.NewPassword()),
            await ResetAsync("", ApiFactory.NewPassword()),
            await ResetAsync(new string('t', 300), ApiFactory.NewPassword())
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Equal("invalid_token", await CodeOf(responses[0]));
    }

    [Fact]
    public async Task Weak_password_is_rejected_and_the_link_still_works()
    {
        var (_, email, _, _) = await UserAsync();
        var token = await RequestTokenAsync(email);

        var weak = await ResetAsync(token, "password");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("NewPassword", await weak.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token, ApiFactory.NewPassword())).StatusCode);
    }

    [Fact]
    public async Task Reset_clears_lockout()
    {
        var (_, email, password, _) = await UserAsync();
        for (var i = 0; i < 5; i++) await LoginAsync(email, "Wrong!Passw0rd");
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, password)).StatusCode);   // locked

        var newPassword = ApiFactory.NewPassword();
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(await RequestTokenAsync(email), newPassword)).StatusCode);

        await LoginOkAsync(email, newPassword);
    }

    // ---- change ----

    [Fact]
    public async Task Change_returns_a_working_session_and_kills_the_old_ones()
    {
        var (_, email, password, _) = await UserAsync();
        var old = await LoginOkAsync(email, password);
        var newPassword = ApiFactory.NewPassword();

        var response = await ChangeAsync(old.AccessToken, password, newPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(fresh.AccessToken).GetAsync("/api/v1/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(fresh.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(old.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, password)).StatusCode);
        await LoginOkAsync(email, newPassword);
        Assert.Equal("Your ZenLead password was changed", MailTo(email).Last().Subject);
    }

    [Fact]
    public async Task Wrong_current_password_counts_toward_lockout_with_the_same_response()
    {
        var (_, email, password, token) = await UserAsync();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await ChangeAsync(token, "Wrong!Passw0rd", ApiFactory.NewPassword());
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("wrong_password", await CodeOf(wrong));
        }

        var locked = await ChangeAsync(token, password, ApiFactory.NewPassword());
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        Assert.Equal("wrong_password", await CodeOf(locked));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, password)).StatusCode);   // the account is locked for login too
    }

    [Fact]
    public async Task New_equal_to_current_and_weak_new_passwords_are_rejected()
    {
        var (_, _, password, token) = await UserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(token, password, password)).StatusCode);
        var weak = await ChangeAsync(token, password, "weak");
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("NewPassword", await weak.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Super_admin_can_change_their_password()
    {
        var (_, email, password) = await SuperAdminWithPasswordAsync();
        var session = await LoginOkAsync(email, password);
        var newPassword = ApiFactory.NewPassword();

        var response = await ChangeAsync(session.AccessToken, password, newPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(fresh.AccessToken).GetAsync("/api/v1/admin/workspaces")).StatusCode);
        await LoginOkAsync(email, newPassword);
    }

    [Fact]
    public async Task Change_password_requires_a_token()
        => Assert.Equal(HttpStatusCode.Unauthorized,
            (await Anonymous.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest("a", "N3w!Passw0rd"))).StatusCode);

    // ---- policy parity ----

    private sealed class RulesOnly : AbstractValidator<string>
    {
        public RulesOnly() => RuleFor(x => x).MustBeStrongPassword();
    }

    [Theory]
    [InlineData("Passw0rd!")]
    [InlineData("password1")]
    [InlineData("PASSWORD1!")]
    [InlineData("Password!!")]
    [InlineData("Password12")]
    [InlineData("Pa1!")]
    [InlineData("Pa1!Pa1!")]
    [InlineData("Pässw0rd!")]
    [InlineData("        ")]
    [InlineData("Aa1 aaaa")]
    public async Task PasswordRules_and_the_Identity_options_agree(string sample)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var identityOk = true;
        foreach (var validator in users.PasswordValidators)
            identityOk &= (await validator.ValidateAsync(users, new AppUser(), sample)).Succeeded;

        Assert.Equal(identityOk, new RulesOnly().Validate(sample).IsValid);
    }
}

public class ForgotPasswordRateLimitTests(ForgotLimitApiFactory factory) : IClassFixture<ForgotLimitApiFactory>
{
    [Fact]
    public async Task Eleventh_forgot_request_in_an_hour_from_one_ip_is_429()
    {
        var client = factory.ClientFor(null);
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest($"n{i}@example.com"))).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("n@example.com"))).StatusCode);
    }
}
