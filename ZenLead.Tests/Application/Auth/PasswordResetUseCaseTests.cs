using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZenLead.Application;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Auth;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Security;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Auth;

/// <summary>F21 use cases over fakes: forgot (no enumeration, throttling, timing shape), reset (single use, policy first), change.</summary>
public class PasswordResetUseCaseTests
{
    private const string GoodPassword = "N3w!Passw0rd";
    private readonly FakeIdentityService _identity = new();
    private readonly FakePasswordResetRepository _tokens = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeRefreshTokenService _refresh = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly PasswordResetOptions _options = new();
    private readonly List<TimeSpan> _delays = [];
    private static readonly IOptions<AppOptions> App = Options.Create(new AppOptions { PublicBaseUrl = "https://app.zenlead.test/" });

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private ForgotPasswordUseCase Forgot(IEmailSender? sender = null) => new(_identity, _tokens, sender ?? _email, App, Options.Create(_options), _clock,
        NullLogger<ForgotPasswordUseCase>.Instance)
    {
        Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; }
    };

    private ResetPasswordUseCase Reset => new(_tokens, _identity, _refresh, _email, App, _clock, NullLogger<ResetPasswordUseCase>.Instance);

    private ChangePasswordUseCase Change => new(_identity, _refresh, new FakeJwtTokenGenerator(), _email, App, NullLogger<ChangePasswordUseCase>.Instance);

    private string LastToken() => _email.Sent.Last().TextBody.Split("token=")[1].Trim();

    /// <summary>Requests a reset for an active user and returns the raw token from the email.</summary>
    private async Task<(FakeIdentityService.FakeUser User, string Token)> IssuedAsync()
    {
        var user = _identity.Add($"{Guid.NewGuid():N}@acme.test", AppUserStatus.Active, Guid.NewGuid());
        await Forgot().ExecuteAsync(user.Email, "203.0.113.9");
        return (user, LastToken());
    }

    // ---- forgot ----

    [Fact]
    public async Task Active_user_gets_one_email_with_a_link_and_only_the_hash_is_stored()
    {
        var user = _identity.Add("alice@acme.test", AppUserStatus.Active, Guid.NewGuid());

        await Forgot().ExecuteAsync("ALICE@acme.test", "203.0.113.9");

        var mail = Assert.Single(_email.Sent);
        Assert.Equal("alice@acme.test", mail.To.Address);
        Assert.Contains("https://app.zenlead.test/reset-password?token=", mail.TextBody);
        var raw = LastToken();
        var row = Assert.Single(_tokens.Rows);
        Assert.Equal(user.Id, row.UserId);
        Assert.Equal(Now.AddHours(1), row.ExpiresAt);
        Assert.Equal(TokenHasher.Hash(raw), row.TokenHash);
        Assert.NotEqual(raw, row.TokenHash);
        Assert.Equal("203.0.113.9", row.RequestedIp);
    }

    [Fact]
    public async Task Unknown_email_sends_nothing_and_stores_nothing()
    {
        await Forgot().ExecuteAsync("nobody@example.com", null);

        Assert.Empty(_email.Sent);
        Assert.Empty(_tokens.Rows);
    }

    [Theory]
    [InlineData(AppUserStatus.PendingApproval)]
    [InlineData(AppUserStatus.Rejected)]
    [InlineData(AppUserStatus.Disabled)]
    public async Task Inactive_users_get_nothing(AppUserStatus status)
    {
        _identity.Add("bob@acme.test", status, Guid.NewGuid());

        await Forgot().ExecuteAsync("bob@acme.test", null);

        Assert.Empty(_email.Sent);
        Assert.Empty(_tokens.Rows);
    }

    [Fact]
    public async Task Super_admin_is_eligible()
    {
        _identity.Add("platform@zenlead.test", AppUserStatus.Active, workspaceId: null, role: PlatformRole.SuperAdmin);

        await Forgot().ExecuteAsync("platform@zenlead.test", null);

        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task A_second_request_invalidates_the_first_link()
    {
        var (user, first) = await IssuedAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Forgot().ExecuteAsync(user.Email, null);
        var second = LastToken();

        Assert.Equal(Now, _tokens.Rows.Single(t => t.TokenHash == TokenHasher.Hash(first)).ExpiresAt);   // collapsed
        Assert.Equal(ResetResult.InvalidToken, await Reset.ExecuteAsync(new ResetPasswordRequest(first, GoodPassword)));
        Assert.True((await Reset.ExecuteAsync(new ResetPasswordRequest(second, GoodPassword))).IsOk);
    }

    [Fact]
    public async Task Fourth_request_in_an_hour_sends_nothing()
    {
        var user = _identity.Add("carol@acme.test", AppUserStatus.Active, Guid.NewGuid());
        for (var i = 0; i < 4; i++)
        {
            await Forgot().ExecuteAsync(user.Email, null);
            _clock.Advance(TimeSpan.FromMinutes(5));
        }
        Assert.Equal(3, _email.Sent.Count);
        Assert.Equal(3, _tokens.Rows.Count);

        _clock.Advance(TimeSpan.FromHours(1));   // the window rolls on
        await Forgot().ExecuteAsync(user.Email, null);
        Assert.Equal(4, _email.Sent.Count);
    }

    [Fact]
    public async Task Response_is_padded_on_both_the_existing_and_the_unknown_path()
    {
        _identity.Add("dave@acme.test", AppUserStatus.Active, Guid.NewGuid());

        await Forgot().ExecuteAsync("dave@acme.test", null);
        await Forgot().ExecuteAsync("nobody@example.com", null);

        Assert.Equal(2, _delays.Count);
        // MinResponseMs (600) minus the (tiny) elapsed time, plus 0–99 ms jitter
        Assert.All(_delays, d => Assert.InRange(d.TotalMilliseconds, 400, 700));
    }

    [Fact]
    public async Task Mail_provider_failure_still_completes_normally_and_is_padded()
    {
        _identity.Add("erin@acme.test", AppUserStatus.Active, Guid.NewGuid());

        await Forgot(new ThrowingEmailSender()).ExecuteAsync("erin@acme.test", null);   // no exception

        Assert.Single(_delays);
    }

    [Fact]
    public async Task Long_ip_is_truncated_to_45_chars()
    {
        var user = _identity.Add("ip@acme.test", AppUserStatus.Active, Guid.NewGuid());
        await Forgot().ExecuteAsync(user.Email, new string('1', 60));
        Assert.Equal(45, _tokens.Rows.Single().RequestedIp!.Length);
    }

    // ---- reset ----

    [Fact]
    public async Task Reset_sets_the_password_revokes_every_session_and_sends_a_notice()
    {
        var (user, token) = await IssuedAsync();

        var result = await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword));

        Assert.True(result.IsOk);
        Assert.Equal(GoodPassword, user.Password);
        Assert.Contains(user.Id, _refresh.RevokedAllFor);
        Assert.Equal("Your ZenLead password was changed", _email.Sent.Last().Subject);
        Assert.Equal(user.Email, _email.Sent.Last().To.Address);
    }

    [Fact]
    public async Task Replay_after_success_is_invalid()
    {
        var (_, token) = await IssuedAsync();
        Assert.True((await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword))).IsOk);

        Assert.Equal(ResetResult.InvalidToken, await Reset.ExecuteAsync(new ResetPasswordRequest(token, "An0ther!Passw0rd")));
    }

    [Fact]
    public async Task Expired_token_is_invalid()
    {
        var (user, token) = await IssuedAsync();
        _clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        Assert.Equal(ResetResult.InvalidToken, await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword)));
        Assert.Empty(_identity.PasswordSetFor);
        Assert.DoesNotContain(user.Id, _refresh.RevokedAllFor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public async Task Unknown_or_empty_token_is_invalid(string token)
    {
        await IssuedAsync();
        Assert.Equal(ResetResult.InvalidToken, await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword)));
    }

    [Fact]
    public async Task Weak_password_reports_policy_errors_and_does_not_burn_the_link()
    {
        var (_, token) = await IssuedAsync();

        var weak = await Reset.ExecuteAsync(new ResetPasswordRequest(token, "password"));
        Assert.False(weak.IsOk);
        Assert.NotEmpty(weak.Errors);
        Assert.Empty(_identity.PasswordSetFor);

        Assert.True((await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword))).IsOk);
    }

    [Fact]
    public async Task Concurrent_submits_with_the_same_token_have_exactly_one_winner()
    {
        var (user, token) = await IssuedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => Task.Run(() => Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword)))));

        Assert.Single(results, r => r.IsOk);
        Assert.Single(_identity.PasswordSetFor, id => id == user.Id);
    }

    [Fact]
    public async Task User_disabled_after_the_email_went_out_cannot_reset()
    {
        var (user, token) = await IssuedAsync();
        user.Status = AppUserStatus.Disabled;

        Assert.Equal(ResetResult.InvalidToken, await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword)));
        Assert.Empty(_identity.PasswordSetFor);
    }

    [Fact]
    public async Task Reset_clears_lockout()
    {
        var (user, token) = await IssuedAsync();
        user.LockedOut = true;
        user.FailedAttempts = 5;

        Assert.True((await Reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword))).IsOk);
        Assert.False(user.LockedOut);
        Assert.Equal(0, user.FailedAttempts);
    }

    [Fact]
    public async Task Notice_email_failure_does_not_undo_the_reset()
    {
        var (user, token) = await IssuedAsync();
        var reset = new ResetPasswordUseCase(_tokens, _identity, _refresh, new ThrowingEmailSender(), App, _clock, NullLogger<ResetPasswordUseCase>.Instance);

        Assert.True((await reset.ExecuteAsync(new ResetPasswordRequest(token, GoodPassword))).IsOk);
        Assert.Equal(GoodPassword, user.Password);
    }

    // ---- change ----

    [Fact]
    public async Task Change_revokes_all_then_issues_a_fresh_session_and_sends_a_notice()
    {
        var user = _identity.Add("frank@acme.test", AppUserStatus.Active, Guid.NewGuid(), password: "Old!Passw0rd");

        var result = await Change.ExecuteAsync(user.Id, new ChangePasswordRequest("Old!Passw0rd", GoodPassword));

        Assert.Equal(ChangePasswordOutcome.Success, result.Outcome);
        Assert.NotNull(result.Tokens);
        Assert.Equal([user.Id], _refresh.RevokedAllFor);
        Assert.Equal([user.Id], _refresh.IssuedFor);
        Assert.Equal(GoodPassword, user.Password);
        Assert.Equal("Your ZenLead password was changed", Assert.Single(_email.Sent).Subject);
    }

    [Fact]
    public async Task Super_admin_change_keeps_the_role_and_no_workspace()
    {
        var admin = _identity.Add("platform@zenlead.test", AppUserStatus.Active, null, password: "Old!Passw0rd", role: PlatformRole.SuperAdmin);

        var result = await Change.ExecuteAsync(admin.Id, new ChangePasswordRequest("Old!Passw0rd", GoodPassword));

        Assert.Equal($"fake-token:{admin.Id}::platform@zenlead.test:SuperAdmin", result.Tokens!.AccessToken);
    }

    [Fact]
    public async Task Wrong_current_password_and_lockout_look_the_same_and_change_nothing()
    {
        var user = _identity.Add("gina@acme.test", AppUserStatus.Active, Guid.NewGuid(), password: "Old!Passw0rd");

        for (var i = 0; i < 5; i++)
            Assert.Equal(ChangePasswordOutcome.WrongPassword, (await Change.ExecuteAsync(user.Id, new ChangePasswordRequest("Wrong!1a", GoodPassword))).Outcome);
        Assert.True(user.LockedOut);

        // locked out: even the right current password gets the same answer
        Assert.Equal(ChangePasswordOutcome.WrongPassword, (await Change.ExecuteAsync(user.Id, new ChangePasswordRequest("Old!Passw0rd", GoodPassword))).Outcome);
        Assert.Equal("Old!Passw0rd", user.Password);
        Assert.Empty(_refresh.RevokedAllFor);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Weak_new_password_is_a_policy_failure()
    {
        var user = _identity.Add("hank@acme.test", AppUserStatus.Active, Guid.NewGuid(), password: "Old!Passw0rd");

        var result = await Change.ExecuteAsync(user.Id, new ChangePasswordRequest("Old!Passw0rd", "weak"));

        Assert.Equal(ChangePasswordOutcome.PolicyFailed, result.Outcome);
        Assert.NotEmpty(result.Errors!);
        Assert.Empty(_refresh.RevokedAllFor);
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default) => throw new HttpRequestException("provider down");
    }
}
