using System.Security.Claims;
using System.Threading.RateLimiting;

namespace ZenLead.Api;

public static class RateLimiting
{
    public const string ComposePolicy = "compose";
    public const int ComposePermitsPerMinute = 10;

    public const string AuthPolicy = "auth";
    public const int AuthPermitsPerMinute = 20;

    /// <summary>One fixed-window bucket per workspace, so one tenant can't burn the shared OpenAI budget.</summary>
    public static RateLimitPartition<string> ComposePartition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("workspace_id") ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ComposePermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });

    /// <summary>Per-client-IP bucket for login / register / refresh, to blunt credential guessing.</summary>
    public static RateLimitPartition<string> AuthPartition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = AuthPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
}
