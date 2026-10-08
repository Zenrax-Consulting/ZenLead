using System.Security.Claims;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Api;
using ZenLead.Api.Controllers.V1;

namespace ZenLead.Tests.Api;

internal class NullStorage : JobStorage
{
    public override IStorageConnection GetConnection() => throw new NotSupportedException();
    public override IMonitoringApi GetMonitoringApi() => throw new NotSupportedException();
}

public class HangfireDashboardAuthFilterTests
{
    private readonly IDataProtectionProvider _dp = new EphemeralDataProtectionProvider();

    private static DashboardContext ContextWithCookie(string? cookie)
    {
        var http = new DefaultHttpContext { RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider() };
        if (cookie is not null) http.Request.Headers.Cookie = $"{OpsController.CookieName}={cookie}";
        return new AspNetCoreDashboardContext(new NullStorage(), new DashboardOptions(), http);
    }

    private string Cookie(DateTimeOffset expires)
        => _dp.CreateProtector(HangfireDashboardAuthFilter.Purpose).Protect(expires.ToUnixTimeSeconds().ToString());

    [Fact]
    public async Task NoCookie_IsDenied()
        => Assert.False(await new HangfireDashboardAuthFilter(_dp).AuthorizeAsync(ContextWithCookie(null)));

    [Fact]
    public async Task ValidCookie_IsAllowed()
        => Assert.True(await new HangfireDashboardAuthFilter(_dp).AuthorizeAsync(ContextWithCookie(Cookie(DateTimeOffset.UtcNow.AddMinutes(5)))));

    [Fact]
    public async Task ExpiredCookie_IsDenied()
        => Assert.False(await new HangfireDashboardAuthFilter(_dp).AuthorizeAsync(ContextWithCookie(Cookie(DateTimeOffset.UtcNow.AddMinutes(-1)))));

    [Fact]
    public async Task TamperedCookie_IsDenied()
    {
        var cookie = Cookie(DateTimeOffset.UtcNow.AddMinutes(5));
        var tampered = cookie[..^3] + (cookie.EndsWith("AAA") ? "BBB" : "AAA");

        Assert.False(await new HangfireDashboardAuthFilter(_dp).AuthorizeAsync(ContextWithCookie(tampered)));
    }

    [Fact]
    public async Task CookieFromADifferentPurpose_IsDenied()
    {
        var other = _dp.CreateProtector("something-else").Protect(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString());

        Assert.False(await new HangfireDashboardAuthFilter(_dp).AuthorizeAsync(ContextWithCookie(other)));
    }

    private OpsController Ops(string? email, params string[] admins)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            admins.Select((a, i) => new KeyValuePair<string, string?>($"Hangfire:AdminEmails:{i}", a))).Build();
        var controller = new OpsController(config, _dp);
        var claims = email is null ? [] : new[] { new Claim("email", email) };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
        };
        return controller;
    }

    [Fact]
    public void Ops_NonAdmin_IsForbidden_AndGetsNoCookie()
    {
        var controller = Ops("user@acme.com", "boss@acme.com");

        var result = controller.OpenDashboard();

        Assert.IsType<ForbidResult>(result);
        Assert.False(controller.HttpContext.Response.Headers.ContainsKey("Set-Cookie"));
        var status = Assert.IsType<OkObjectResult>(controller.Status().Result).Value!;
        Assert.Equal(false, status.GetType().GetProperty("canOpenDashboard")!.GetValue(status));
    }

    [Fact]
    public void Ops_Admin_GetsCookie_CaseInsensitively()
    {
        var controller = Ops("BOSS@acme.com", "boss@acme.com");

        var result = controller.OpenDashboard();

        Assert.IsType<NoContentResult>(result);
        Assert.Contains(OpsController.CookieName, controller.HttpContext.Response.Headers.SetCookie.ToString());
    }
}
