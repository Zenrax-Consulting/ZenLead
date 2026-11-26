namespace ZenLead.Application.Auth;

/// <summary>Bound from the "PasswordReset" config section (F21).</summary>
public class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Reset emails per account per rolling hour; further requests are silently dropped.</summary>
    public int MaxPerEmailPerHour { get; set; } = 3;

    /// <summary>Forgot-password responses are padded to at least this long (plus jitter) on every path, so timing doesn't reveal accounts.</summary>
    public int MinResponseMs { get; set; } = 600;
}
