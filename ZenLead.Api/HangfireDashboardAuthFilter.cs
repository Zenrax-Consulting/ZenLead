using System.Globalization;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.DataProtection;

namespace ZenLead.Api;

/// <summary>Lets a browser navigation into /hangfire through only with the short-lived cookie issued by <c>POST /api/v1/ops/hangfire-session</c>.</summary>
public class HangfireDashboardAuthFilter(IDataProtectionProvider dataProtection, TimeProvider? clock = null) : IDashboardAsyncAuthorizationFilter
{
    public const string Purpose = "hangfire-dashboard";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<bool> AuthorizeAsync(DashboardContext context)
    {
        try
        {
            var cookie = context.GetHttpContext().Request.Cookies[Controllers.V1.OpsController.CookieName];
            if (string.IsNullOrEmpty(cookie)) return Task.FromResult(false);

            var expiry = long.Parse(dataProtection.CreateProtector(Purpose).Unprotect(cookie), CultureInfo.InvariantCulture);
            return Task.FromResult(_clock.GetUtcNow().ToUnixTimeSeconds() < expiry);
        }
        catch
        {
            return Task.FromResult(false);      // tampered, malformed or from a lost key ring
        }
    }
}
