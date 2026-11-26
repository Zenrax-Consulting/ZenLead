using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = Policies.AnyUser)]   // not tenant data; the super admin may also be in Hangfire:AdminEmails
[Route("api/v1/ops")]
public class OpsController(IConfiguration config, IDataProtectionProvider dp, IEmailSender sender) : ControllerBase
{
    public const string CookieName = "zl_ops";

    [HttpGet("status")]
    public ActionResult<object> Status() => Ok(new { canOpenDashboard = IsOps() });

    [HttpPost("hangfire-session")]
    public IActionResult OpenDashboard()
    {
        if (!IsOps()) return Forbid();
        var protector = dp.CreateProtector(HangfireDashboardAuthFilter.Purpose);
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        Response.Cookies.Append(CookieName, protector.Protect(expires.ToUnixTimeSeconds().ToString()), new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/hangfire", Expires = expires
        });
        return NoContent();
    }

    /// <summary>Sends a test email to the calling ops user's own address, proving outbound mail works end to end.</summary>
    [HttpPost("test-email")]
    [EnableRateLimiting(RateLimiting.AuthPolicy)]
    public async Task<IActionResult> TestEmail(CancellationToken ct)
    {
        if (!IsOps()) return Forbid();
        var to = User.FindFirstValue("email")!;                  // always the caller's own address — never a free-form recipient
        var result = await sender.SendAsync(SystemEmail.Create(to, null, "ZenLead test email",
            "If you can read this, outbound email from ZenLead is working."), ct);
        return result.Success
            ? Ok(new { sent = true, providerMessageId = result.ProviderMessageId })
            : StatusCode(StatusCodes.Status502BadGateway, new { sent = false, error = result.Error });
    }

    private bool IsOps()
    {
        var email = User.FindFirstValue("email");
        var allowed = config.GetSection("Hangfire:AdminEmails").Get<string[]>() ?? [];
        return email is not null && allowed.Contains(email, StringComparer.OrdinalIgnoreCase);
    }
}
