using System.Security.Claims;
using System.Threading.RateLimiting;

namespace ZenLead.Api;

public static class RateLimiting
{
    public const string ComposePolicy = "compose";
    public const int ComposePermitsPerMinute = 10;

    public const string DiscoveryPolicy = "discovery";
    public const int DiscoveryPermitsPerMinute = 5;

    public const string UploadPolicy = "upload";
    public const int UploadPermitsPerMinute = 5;

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

    /// <summary>One bucket per workspace so starting discovery runs cannot burn the shared provider credits.</summary>
    public static RateLimitPartition<string> DiscoveryPartition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("workspace_id") ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = DiscoveryPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });

    /// <summary>One bucket per workspace so repeated CSV uploads cannot fill storage or tie up the parser.</summary>
    public static RateLimitPartition<string> UploadPartition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("workspace_id") ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = UploadPermitsPerMinute,
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
