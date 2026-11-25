using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Tests.Api.Support;

/// <summary>Identity lockout through the real login endpoint (5 failures → 15-minute lockout, same generic 401).</summary>
public class LockoutTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(string Email, string Password)> NewUserAsync()
    {
        var email = $"{Guid.NewGuid():N}@acme.test";
        var password = ApiFactory.NewPassword();
        await factory.CreateWorkspaceAndUserAsync($"Acme {Guid.NewGuid():N}", email, password);
        return (email, password);
    }

    private static string WithoutTraceId(string body) => Regex.Replace(body, "\"traceId\":\"[^\"]*\"", "");

    private Task<HttpResponseMessage> LoginAsync(string email, string password)
        => factory.ClientFor(null).PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var (email, password) = await NewUserAsync();

        HttpResponseMessage wrong = null!;
        for (var i = 0; i < 5; i++)
        {
            wrong = await LoginAsync(email, "Wrong!Passw0rd");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var locked = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        // nothing that tells a locked account apart from a wrong password (only the per-request trace id differs)
        Assert.Equal(WithoutTraceId(await wrong.Content.ReadAsStringAsync()), WithoutTraceId(await locked.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var (email, password) = await NewUserAsync();

        for (var i = 0; i < 4; i++) await LoginAsync(email, "Wrong!Passw0rd");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, password)).StatusCode);

        for (var i = 0; i < 4; i++) await LoginAsync(email, "Wrong!Passw0rd");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, password)).StatusCode);
    }
}
