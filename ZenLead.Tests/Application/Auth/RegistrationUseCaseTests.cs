using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZenLead.Application;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Auth;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Security;
using ZenLead.Application.UseCases.Auth;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Auth;

public class RegistrationUseCaseTests
{
    private readonly FakeWorkspaceRepository _workspaces = new();
    private readonly FakeIdentityService _identity = new();
    private readonly FakeApprovalRepository _approvals = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeRefreshTokenService _refresh = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly RegistrationOptions _options = new();
    private static readonly IOptions<AppOptions> App = Options.Create(new AppOptions { PublicBaseUrl = "https://app.zenlead.test/" });

    private RequestRegistrationUseCase Register => new(_workspaces, _identity, _approvals, _email, App, Options.Create(_options), _clock,
        NullLogger<RequestRegistrationUseCase>.Instance);
    private GetApprovalInfoUseCase Info => new(_approvals, _identity, _workspaces, _clock);
    private DecideApprovalUseCase Decide => new(_approvals, _identity, _refresh, _email, App, _clock, NullLogger<DecideApprovalUseCase>.Instance);

    private async Task<Workspace> ZenraxAsync(string? adminEmail = "owner@zenrax.test") => await _workspaces.CreateAsync("Zenrax", adminEmail);

    private static RegisterRequest Request(Guid workspaceId, string email = "colleague@zenrax.test", string name = "Colin")
        => new(email, "Passw0rd!", name, workspaceId);

    /// <summary>The raw token as it appears in the last approval email's link.</summary>
    private string LastToken() => _email.Sent.Last().TextBody.Split("token=")[1].Trim();

    // ---- registration ----

    [Fact]
    public async Task Unknown_workspace_creates_no_user()
    {
        Assert.Equal(RegistrationOutcome.UnknownWorkspace, await Register.ExecuteAsync(Request(Guid.NewGuid())));
        Assert.Empty(_identity.Users);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Workspace_without_admin_email_is_not_accepting()
    {
        var ws = await ZenraxAsync(adminEmail: null);
        Assert.Equal(RegistrationOutcome.WorkspaceNotAccepting, await Register.ExecuteAsync(Request(ws.Id)));
        Assert.Empty(_identity.Users);
    }

    [Fact]
    public async Task New_email_creates_pending_user_one_approval_and_one_email_to_the_admin()
    {
        var ws = await ZenraxAsync();

        Assert.Equal(RegistrationOutcome.Accepted, await Register.ExecuteAsync(Request(ws.Id)));

        var user = Assert.Single(_identity.Users.Values);
        Assert.Equal(AppUserStatus.PendingApproval, user.Status);
        Assert.Equal(ws.Id, user.WorkspaceId);

        var approval = Assert.Single(_approvals.Rows);
        Assert.Equal(user.Id, approval.UserId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(7), approval.ExpiresAt);

        var mail = Assert.Single(_email.Sent);
        Assert.Equal("owner@zenrax.test", mail.To.Address);
        Assert.Contains("Colin", mail.Subject);
        Assert.Contains("colleague@zenrax.test", mail.TextBody);
        Assert.Contains("https://app.zenlead.test/approve?token=", mail.TextBody);
        Assert.Equal(TokenHasher.Hash(LastToken()), approval.TokenHash);      // the raw token is never stored
        Assert.DoesNotContain(LastToken(), approval.TokenHash);
    }

    [Fact]
    public async Task Token_is_url_safe_and_256_bit()
    {
        var ws = await ZenraxAsync();
        await Register.ExecuteAsync(Request(ws.Id));

        var token = LastToken();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
    }

    [Fact]
    public async Task Display_name_is_html_encoded_in_the_email()
    {
        var ws = await ZenraxAsync();
        await Register.ExecuteAsync(Request(ws.Id, name: "<b>x</b>"));

        var mail = Assert.Single(_email.Sent);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<b>x</b>", mail.HtmlBody);
    }

    [Theory]
    [InlineData(AppUserStatus.PendingApproval)]
    [InlineData(AppUserStatus.Active)]
    [InlineData(AppUserStatus.Rejected)]
    [InlineData(AppUserStatus.Disabled)]
    public async Task Existing_email_is_accepted_without_creating_anything(AppUserStatus status)
    {
        var ws = await ZenraxAsync();
        var existing = _identity.Add("colleague@zenrax.test", status, ws.Id);
        _approvals.Rows.Add(new RegistrationApproval { UserId = existing.Id, TokenHash = "x", CreatedAt = _clock.GetUtcNow().UtcDateTime });

        Assert.Equal(RegistrationOutcome.Accepted, await Register.ExecuteAsync(Request(ws.Id)));

        Assert.Single(_identity.Users);
        Assert.Empty(_email.Sent);
        Assert.Equal(1, _identity.BurnCount);                                   // same work shape as creating a user
    }

    [Fact]
    public async Task Repeat_request_inside_an_hour_sends_nothing_after_an_hour_sends_a_new_link_and_kills_the_old()
    {
        var ws = await ZenraxAsync();
        await Register.ExecuteAsync(Request(ws.Id));
        var first = LastToken();

        _clock.Advance(TimeSpan.FromMinutes(59));
        await Register.ExecuteAsync(Request(ws.Id));
        Assert.Single(_email.Sent);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await Register.ExecuteAsync(Request(ws.Id));
        Assert.Equal(2, _email.Sent.Count);
        var second = LastToken();
        Assert.NotEqual(first, second);

        Assert.Equal(DecisionResult.Expired, await Decide.ExecuteAsync(first, ApprovalDecision.Approved));
        Assert.Equal(DecisionResult.Done, await Decide.ExecuteAsync(second, ApprovalDecision.Approved));
    }

    [Fact]
    public async Task Pending_user_asking_for_a_different_workspace_gets_no_email()
    {
        var ws = await ZenraxAsync();
        var other = await _workspaces.CreateAsync("Other", "boss@other.test");
        _identity.Add("colleague@zenrax.test", AppUserStatus.PendingApproval, other.Id);

        Assert.Equal(RegistrationOutcome.Accepted, await Register.ExecuteAsync(Request(ws.Id)));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Pending_cap_refuses_the_21st_request()
    {
        var ws = await ZenraxAsync();
        for (var i = 0; i < 20; i++)
            Assert.Equal(RegistrationOutcome.Accepted, await Register.ExecuteAsync(Request(ws.Id, $"u{i}@zenrax.test")));

        Assert.Equal(RegistrationOutcome.TooManyPending, await Register.ExecuteAsync(Request(ws.Id, "u20@zenrax.test")));
        Assert.Equal(20, _identity.Users.Count);
    }

    [Fact]
    public async Task Losing_a_create_race_on_the_same_email_is_still_accepted()
    {
        var ws = await ZenraxAsync();
        _identity.ExceptionOnCreate = new EmailAlreadyRegisteredException();

        Assert.Equal(RegistrationOutcome.Accepted, await Register.ExecuteAsync(Request(ws.Id)));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Weak_password_rejected_by_identity_propagates()
    {
        var ws = await ZenraxAsync();
        _identity.ExceptionOnCreate = new RegistrationFailedException(["too weak"]);

        await Assert.ThrowsAsync<RegistrationFailedException>(() => Register.ExecuteAsync(Request(ws.Id)));
    }

    [Fact]
    public async Task Failed_email_send_still_answers_accepted()
    {
        var ws = await ZenraxAsync();
        _email.Result = SendResult.Fail("down", retryable: true);

        Assert.Equal(RegistrationOutcome.Accepted, await Register.ExecuteAsync(Request(ws.Id)));
        Assert.Single(_approvals.Rows);
    }

    // ---- approval ----

    private async Task<(Guid UserId, string Token)> RegisterPendingAsync()
    {
        var ws = await ZenraxAsync();
        await Register.ExecuteAsync(Request(ws.Id));
        var token = LastToken();
        _email.Sent.Clear();
        return (_identity.Users.Values.Single().Id, token);
    }

    [Fact]
    public async Task Approve_activates_the_user_and_emails_them()
    {
        var (userId, token) = await RegisterPendingAsync();

        Assert.Equal(DecisionResult.Done, await Decide.ExecuteAsync(token, ApprovalDecision.Approved));

        Assert.Equal(AppUserStatus.Active, _identity.Users[userId].Status);
        var mail = Assert.Single(_email.Sent);
        Assert.Equal("colleague@zenrax.test", mail.To.Address);
        Assert.Contains("https://app.zenlead.test/login", mail.TextBody);
    }

    [Fact]
    public async Task Reject_marks_rejected_revokes_sessions_and_sends_nothing()
    {
        var (userId, token) = await RegisterPendingAsync();

        Assert.Equal(DecisionResult.Done, await Decide.ExecuteAsync(token, ApprovalDecision.Rejected));

        Assert.Equal(AppUserStatus.Rejected, _identity.Users[userId].Status);
        Assert.Equal([userId], _refresh.RevokedAllFor);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Replay_is_already_used_and_changes_nothing()
    {
        var (userId, token) = await RegisterPendingAsync();
        await Decide.ExecuteAsync(token, ApprovalDecision.Approved);
        _email.Sent.Clear();

        Assert.Equal(DecisionResult.AlreadyUsed, await Decide.ExecuteAsync(token, ApprovalDecision.Rejected));
        Assert.Equal(AppUserStatus.Active, _identity.Users[userId].Status);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Expired_after_seven_days()
    {
        var (userId, token) = await RegisterPendingAsync();
        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));

        Assert.Equal(DecisionResult.Expired, await Decide.ExecuteAsync(token, ApprovalDecision.Approved));
        Assert.Equal(AppUserStatus.PendingApproval, _identity.Users[userId].Status);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    public async Task Unknown_token_is_not_found(string token)
    {
        await RegisterPendingAsync();
        Assert.Equal(DecisionResult.NotFound, await Decide.ExecuteAsync(token, ApprovalDecision.Approved));
        Assert.Null(await Info.ExecuteAsync(token));
    }

    [Fact]
    public async Task User_disabled_meanwhile_burns_the_token_without_changing_state()
    {
        var (userId, token) = await RegisterPendingAsync();
        await _identity.SetStatusAsync(userId, AppUserStatus.Disabled);

        Assert.Equal(DecisionResult.Done, await Decide.ExecuteAsync(token, ApprovalDecision.Approved));
        Assert.Equal(AppUserStatus.Disabled, _identity.Users[userId].Status);
        Assert.Equal(DecisionResult.AlreadyUsed, await Decide.ExecuteAsync(token, ApprovalDecision.Approved));
    }

    [Fact]
    public async Task Info_shows_details_while_pending_and_only_the_status_afterwards()
    {
        var (userId, token) = await RegisterPendingAsync();

        var pending = await Info.ExecuteAsync(token);
        Assert.Equal(new ApprovalInfoResponse("pending", "Colin", "colleague@zenrax.test", "Zenrax"), pending);
        Assert.Equal(AppUserStatus.PendingApproval, _identity.Users[userId].Status);   // reading never changes state

        await Decide.ExecuteAsync(token, ApprovalDecision.Approved);
        Assert.Equal(new ApprovalInfoResponse("used", null, null, null), await Info.ExecuteAsync(token));
    }

    [Fact]
    public async Task Info_for_expired_token_shows_status_only()
    {
        var (_, token) = await RegisterPendingAsync();
        _clock.Advance(TimeSpan.FromDays(8));

        Assert.Equal(new ApprovalInfoResponse("expired", null, null, null), await Info.ExecuteAsync(token));
    }
}
