using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/ops")]
public class OpsController(IConfiguration config, IDataProtectionProvider dp) : ControllerBase
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

    private bool IsOps()
    {
        var email = User.FindFirstValue("email");
        var allowed = config.GetSection("Hangfire:AdminEmails").Get<string[]>() ?? [];
        return email is not null && allowed.Contains(email, StringComparer.OrdinalIgnoreCase);
    }
}
