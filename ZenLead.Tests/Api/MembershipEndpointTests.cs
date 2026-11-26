using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Api.Support;

namespace ZenLead.Tests.Api;

/// <summary>ApiFactory with the per-IP limits raised: every test request comes from the same "IP".</summary>
public class MembershipApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimits:AuthPermitLimit", "10000");
        builder.UseSetting("RateLimits:RegisterPermitLimit", "10000");
        builder.UseSetting("RateLimits:PickerPermitLimit", "10000");
    }
}

/// <summary>F20 end to end: register → approval email → approve/reject → login/refresh, the picker, and data sharing.</summary>
public class MembershipEndpointTests(MembershipApiFactory factory) : IClassFixture<MembershipApiFactory>
{
    private HttpClient Anonymous => factory.ClientFor(null);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
    private static string NewEmail(string prefix = "user") => $"{Unique(prefix)}@zenrax.test";

    private async Task<(Guid Id, string Name, string AdminEmail)> WorkspaceAsync(string? adminEmail = null, string? name = null)
    {
        name ??= Unique("Zenrax");
        adminEmail ??= NewEmail("owner");
        using var scope = factory.Services.CreateScope();
        var ws = await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().CreateAsync(name, adminEmail);
        return (ws.Id, name, adminEmail);
    }

    private Task<HttpResponseMessage> RegisterAsync(Guid workspaceId, string email, string password, string name = "Colin")
        => Anonymous.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, password, name, workspaceId));

    private Task<HttpResponseMessage> LoginAsync(string email, string password)
        => Anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));

    private OutboundEmail[] MailTo(string address) => factory.Email.Sent.Where(m => m.To.Address == address).ToArray();
    private static string TokenIn(OutboundEmail mail) => mail.TextBody.Split("token=")[1].Trim();

    private Task<AppUserStatus?> StatusOfAsync(string email)
        => factory.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => (AppUserStatus?)u.Status).FirstOrDefaultAsync());

    /// <summary>Registers <paramref name="email"/> and returns the raw approval token from the admin's mail.</summary>
    private async Task<string> RegisterPendingAsync((Guid Id, string Name, string AdminEmail) ws, string email, string password)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(ws.Id, email, password)).StatusCode);
        return TokenIn(MailTo(ws.AdminEmail).Last());
    }

    private async Task<string> RegisterAndApproveAsync((Guid Id, string Name, string AdminEmail) ws, string email, string password)
    {
        var token = await RegisterPendingAsync(ws, email, password);
        Assert.Equal(HttpStatusCode.NoContent, (await Anonymous.PostAsync($"/api/v1/auth/approvals/{token}/approve", null)).StatusCode);
        var login = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    // ---- registration ----

    [Fact]
    public async Task Unknown_workspace_is_400_and_creates_no_user()
    {
        var email = NewEmail();
        var response = await RegisterAsync(Guid.NewGuid(), email, ApiFactory.NewPassword());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WorkspaceId", await response.Content.ReadAsStringAsync());
        Assert.Null(await StatusOfAsync(email));
    }

    [Fact]
    public async Task Workspace_without_admin_email_is_409()
    {
        var name = Unique("Legacy");
        Guid id;
        using (var scope = factory.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>().CreateAsync(name)).Id;

        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(id, NewEmail(), ApiFactory.NewPassword())).StatusCode);
    }

    [Fact]
    public async Task New_email_is_pending_with_one_approval_row_and_one_mail_to_the_admin()
    {
        var ws = await WorkspaceAsync();
        var email = NewEmail("colleague");

        var response = await RegisterAsync(ws.Id, email, ApiFactory.NewPassword(), "<b>x</b>");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(AppUserStatus.PendingApproval, await StatusOfAsync(email));
        var userId = await factory.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        Assert.Equal(1, await factory.WithDbAsync(db => db.RegistrationApprovals.CountAsync(a => a.UserId == userId)));

        var mail = Assert.Single(MailTo(ws.AdminEmail));
        Assert.Contains(email, mail.TextBody);
        Assert.Contains("https://app.zenlead.test/approve?token=", mail.TextBody);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<b>x</b>", mail.HtmlBody);
    }

    [Fact]
    public async Task Same_202_body_for_new_pending_active_and_rejected_addresses()
    {
        var ws = await WorkspaceAsync();
        var password = ApiFactory.NewPassword();

        var pending = NewEmail("pending");
        await RegisterPendingAsync(ws, pending, password);
        var active = NewEmail("active");
        await RegisterAndApproveAsync(ws, active, password);
        var rejected = NewEmail("rejected");
        var rejectToken = await RegisterPendingAsync(ws, rejected, password);
        await Anonymous.PostAsync($"/api/v1/auth/approvals/{rejectToken}/reject", null);

        var bodies = new List<string>();
        foreach (var email in new[] { NewEmail("fresh"), pending, active, rejected })
        {
            var response = await RegisterAsync(ws.Id, email, password);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task Weak_password_is_400()
        => Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync((await WorkspaceAsync()).Id, NewEmail(), "password")).StatusCode);

    [Fact]
    public async Task Pending_cap_refuses_the_21st_request_and_creates_no_user()
    {
        var ws = await WorkspaceAsync();
        var password = ApiFactory.NewPassword();
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(ws.Id, NewEmail($"cap{i}"), password)).StatusCode);

        var last = NewEmail("cap20");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RegisterAsync(ws.Id, last, password)).StatusCode);
        Assert.Null(await StatusOfAsync(last));
    }

    // ---- approval token ----

    [Fact]
    public async Task Approve_activates_emails_the_user_and_a_replay_is_409()
    {
        var ws = await WorkspaceAsync();
        var email = NewEmail("colleague");
        var password = ApiFactory.NewPassword();
        var token = await RegisterPendingAsync(ws, email, password);

        // opening the link never changes state
        var info = await Anonymous.GetFromJsonAsync<ApprovalInfoResponse>($"/api/v1/auth/approvals/{token}");
        Assert.Equal(new ApprovalInfoResponse("pending", "Colin", email, ws.Name), info);
        Assert.Equal(AppUserStatus.PendingApproval, await StatusOfAsync(email));

        Assert.Equal(HttpStatusCode.NoContent, (await Anonymous.PostAsync($"/api/v1/auth/approvals/{token}/approve", null)).StatusCode);
        Assert.Equal(AppUserStatus.Active, await StatusOfAsync(email));
        var approved = Assert.Single(MailTo(email));
        Assert.Contains("https://app.zenlead.test/login", approved.TextBody);

        var replay = await Anonymous.PostAsync($"/api/v1/auth/approvals/{token}/reject", null);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("already_used", await replay.Content.ReadAsStringAsync());
        Assert.Equal(AppUserStatus.Active, await StatusOfAsync(email));

        var used = await Anonymous.GetStringAsync($"/api/v1/auth/approvals/{token}");
        Assert.Contains("\"used\"", used);
        Assert.DoesNotContain(email, used);                                   // no requester details once used
    }

    [Fact]
    public async Task Reject_sends_no_mail_and_login_is_not_approved()
    {
        var ws = await WorkspaceAsync();
        var email = NewEmail("stranger");
        var password = ApiFactory.NewPassword();
        var token = await RegisterPendingAsync(ws, email, password);

        Assert.Equal(HttpStatusCode.NoContent, (await Anonymous.PostAsync($"/api/v1/auth/approvals/{token}/reject", null)).StatusCode);

        Assert.Equal(AppUserStatus.Rejected, await StatusOfAsync(email));
        Assert.Empty(MailTo(email));
        var login = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("not_approved", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Expired_token_is_410()
    {
        var ws = await WorkspaceAsync();
        var token = await RegisterPendingAsync(ws, NewEmail(), ApiFactory.NewPassword());
        var hash = ZenLead.Application.Security.TokenHasher.Hash(token);
        await factory.WithDbAsync(db => db.RegistrationApprovals.Where(a => a.TokenHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));

        var response = await Anonymous.PostAsync($"/api/v1/auth/approvals/{token}/approve", null);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("\"expired\"", await Anonymous.GetStringAsync($"/api/v1/auth/approvals/{token}"));
    }

    [Fact]
    public async Task Wrong_or_tampered_token_is_404()
    {
        var ws = await WorkspaceAsync();
        var token = await RegisterPendingAsync(ws, NewEmail(), ApiFactory.NewPassword());
        var tampered = (token[0] == 'A' ? "B" : "A") + token[1..];

        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous.PostAsync($"/api/v1/auth/approvals/{tampered}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Anonymous.GetAsync($"/api/v1/auth/approvals/{tampered}")).StatusCode);
    }

    // ---- login / refresh ----

    [Fact]
    public async Task Pending_user_gets_403_with_the_right_password_and_a_plain_401_with_a_wrong_one()
    {
        var ws = await WorkspaceAsync();
        var email = NewEmail();
        var password = ApiFactory.NewPassword();
        await RegisterPendingAsync(ws, email, password);

        var right = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, right.StatusCode);
        Assert.Contains("pending_approval", await right.Content.ReadAsStringAsync());

        var wrong = await LoginAsync(email, "Wrong!Passw0rd");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.DoesNotContain("pending", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Disabling_a_user_kills_their_refresh_token()
    {
        var ws = await WorkspaceAsync();
        var email = NewEmail();
        var password = ApiFactory.NewPassword();
        await RegisterAndApproveAsync(ws, email, password);
        var tokens = await (await LoginAsync(email, password)).Content.ReadFromJsonAsync<AuthResponse>();

        await factory.WithDbAsync(db => db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, AppUserStatus.Disabled)));

        var refresh = await Anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(tokens!.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        var userId = await factory.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync());
        Assert.False(await factory.WithDbAsync(db => db.RefreshTokens.AnyAsync(t => t.UserId == userId && t.RevokedAt == null)));

        var login = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("not_approved", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Super_admin_can_still_refresh()
    {
        var (userId, _) = await factory.CreateSuperAdminAsync();
        string refreshToken;
        using (var scope = factory.Services.CreateScope())
            refreshToken = await scope.ServiceProvider.GetRequiredService<IRefreshTokenService>().IssueAsync(userId);

        Assert.Equal(HttpStatusCode.OK, (await Anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken))).StatusCode);
    }

    // ---- sharing & bootstrapping ----

    [Fact]
    public async Task Bootstrapped_admin_and_approved_colleague_share_leads_and_other_workspaces_stay_isolated()
    {
        var adminEmail = NewEmail("owner");
        var ws = await WorkspaceAsync(adminEmail);
        var password = ApiFactory.NewPassword();

        // the admin registers with the workspace's admin address, gets their own link, approves themselves
        var adminToken = await RegisterAndApproveAsync(ws, adminEmail, password);
        var colleagueToken = await RegisterAndApproveAsync(ws, NewEmail("colleague"), password);

        var created = await factory.ClientFor(adminToken).PostAsJsonAsync("/api/v1/leads",
            new ZenLead.Application.Dtos.Leads.CreateLeadRequest("Ada Lovelace", $"{Guid.NewGuid():N}@prospect.test", "CTO"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var leadId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var seen = JsonDocument.Parse(await factory.ClientFor(colleagueToken).GetStringAsync("/api/v1/leads"));
        Assert.Contains(seen.RootElement.GetProperty("items").EnumerateArray(), l => l.GetProperty("id").GetGuid() == leadId);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(colleagueToken).GetAsync($"/api/v1/leads/{leadId}")).StatusCode);

        var (_, _, outsiderToken) = await factory.CreateWorkspaceAndUserAsync(Unique("Other"), NewEmail("outsider"));
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(outsiderToken).GetAsync($"/api/v1/leads/{leadId}")).StatusCode);
    }

    // ---- picker ----

    [Fact]
    public async Task Picker_returns_ids_and_names_only_anonymously()
    {
        var ws = await WorkspaceAsync(name: Unique("PickerZen"));

        var json = await Anonymous.GetStringAsync("/api/v1/workspaces?q=pickerzen");

        using var doc = JsonDocument.Parse(json);
        var item = Assert.Single(doc.RootElement.EnumerateArray(), e => e.GetProperty("id").GetGuid() == ws.Id);
        Assert.Equal(ws.Name, item.GetProperty("name").GetString());
        Assert.Equal(["id", "name"], item.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("adminEmail", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ws.AdminEmail, json);
    }

    [Fact]
    public async Task Picker_returns_at_most_20()
    {
        var prefix = Unique("Many");
        for (var i = 0; i < 25; i++) await WorkspaceAsync(name: $"{prefix} {i:00}");

        var items = await Anonymous.GetFromJsonAsync<WorkspaceOption[]>($"/api/v1/workspaces?q={prefix}");

        Assert.Equal(20, items!.Length);
        Assert.Equal(items.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => i.Name), items.Select(i => i.Name));
    }

    [Fact]
    public async Task Picker_treats_percent_and_underscore_literally()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var literal = await WorkspaceAsync(name: $"Lit{tag} 100%_off");
        await WorkspaceAsync(name: $"Lit{tag} 100Xoff");

        var percent = await Anonymous.GetFromJsonAsync<WorkspaceOption[]>($"/api/v1/workspaces?q={Uri.EscapeDataString($"{tag} 100%")}");
        Assert.Equal([literal.Id], percent!.Select(o => o.Id));

        var underscore = await Anonymous.GetFromJsonAsync<WorkspaceOption[]>($"/api/v1/workspaces?q={Uri.EscapeDataString($"{tag} 100%_")}");
        Assert.Equal([literal.Id], underscore!.Select(o => o.Id));
    }
}

/// <summary>Default (production) per-IP limits, each class with its own factory so other tests don't use up the window.</summary>
public class RegisterRateLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Sixth_registration_from_one_ip_within_ten_minutes_is_429()
    {
        var client = factory.ClientFor(null);
        for (var i = 0; i < 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests,
                (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@x.test", ApiFactory.NewPassword(), "X", Guid.NewGuid()))).StatusCode);

        var sixth = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest($"{Guid.NewGuid():N}@x.test", ApiFactory.NewPassword(), "X", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }
}

public class PickerRateLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Thirty_first_picker_call_in_a_minute_is_429()
    {
        var client = factory.ClientFor(null);
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/workspaces?q=a")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/v1/workspaces?q=a")).StatusCode);
    }
}
