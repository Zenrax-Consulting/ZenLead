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

    /// <summary>Registration requests (F20): 5 per 10 minutes per IP by default; override with RateLimits:RegisterPermitLimit.</summary>
    public const string RegisterPolicy = "register";
    public const int RegisterPermitsPerWindow = 5;
    public static readonly TimeSpan RegisterWindow = TimeSpan.FromMinutes(10);

    /// <summary>Anonymous workspace picker (F20): 30 per minute per IP by default; override with RateLimits:PickerPermitLimit.</summary>
    public const string PickerPolicy = "picker";
    public const int PickerPermitsPerMinute = 30;

    /// <summary>Forgot-password (F21): 10 per hour per IP by default; override with RateLimits:ForgotPermitLimit. The per-email cap lives in the use case.</summary>
    public const string ForgotPolicy = "forgot";
    public const int ForgotPermitsPerWindow = 10;
    public static readonly TimeSpan ForgotWindow = TimeSpan.FromHours(1);

    /// <summary>Per-client-IP fixed window, for anonymous endpoints.</summary>
    public static RateLimitPartition<string> PerIpPartition(HttpContext context, int permitLimit, TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 });

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
        => AuthPartition(context, AuthPermitsPerMinute);

    public static RateLimitPartition<string> AuthPartition(HttpContext context, int permitsPerMinute)
        => PerIpPartition(context, permitsPerMinute, TimeSpan.FromMinutes(1));
}
