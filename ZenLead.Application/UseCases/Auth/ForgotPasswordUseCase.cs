using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Auth;
using ZenLead.Application.Email;
using ZenLead.Application.Security;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Auth;

/// <summary>
/// Emails a single-use reset link to an active account (F21). The caller always sees the same outcome: no account enumeration,
/// and every path is padded to <see cref="PasswordResetOptions.MinResponseMs"/> plus jitter so timing doesn't tell either.
/// The email is sent inline, not from a background job, so the raw link never sits in job storage.
/// </summary>
public class ForgotPasswordUseCase(
    IIdentityService identity,
    IPasswordResetRepository tokens,
    IEmailSender email,
    IOptions<AppOptions> app,
    IOptions<PasswordResetOptions> options,
    TimeProvider clock,
    ILogger<ForgotPasswordUseCase> logger)
{
    /// <summary>How the response is padded. Tests replace it with a recorder.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

    /// <summary>Always completes the same way for the caller; the only observable effect of an eligible account is the email.</summary>
    public async Task ExecuteAsync(string emailAddress, string? requestedIp, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await TryIssueAsync(emailAddress, requestedIp, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // never reveal failures (e.g. mail provider down) to an anonymous caller
            logger.LogWarning(ex, "Password reset request could not be completed.");
        }

        var remaining = TimeSpan.FromMilliseconds(options.Value.MinResponseMs) - Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
            await Delay(remaining + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 100)), ct);   // pad + jitter
    }

    private async Task TryIssueAsync(string emailAddress, string? ip, CancellationToken ct)
    {
        var user = await identity.FindByEmailAsync(emailAddress.Trim(), ct);
        if (user is null || user.Status != AppUserStatus.Active) return;                        // pending/rejected/disabled/unknown: silent

        var now = clock.GetUtcNow().UtcDateTime;
        if (await tokens.CountCreatedSinceAsync(user.Id, now.AddHours(-1), ct) >= options.Value.MaxPerEmailPerHour)
            return;                                                                               // throttled: silent

        await tokens.InvalidateUnusedAsync(user.Id, now, ct);                                    // only the newest link works
        var raw = TokenHasher.NewUrlSafeToken();
        await tokens.AddAsync(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now,
            ExpiresAt = now.Add(options.Value.TokenLifetime),
            RequestedIp = ip?[..Math.Min(ip.Length, 45)]
        }, ct);

        var result = await email.SendAsync(SystemEmail.Create(user.Email, user.DisplayName, "Reset your ZenLead password",
            "Someone asked to reset the password for this account. If that was you, use the link below within 1 hour. "
            + "If not, you can ignore this email — your password hasn't changed.",
            $"{app.Value.PublicBaseUrl.TrimEnd('/')}/reset-password?token={raw}", "Choose a new password"), ct);
        if (!result.Success) logger.LogWarning("Password reset email for user {UserId} was not sent: {Error}", user.Id, result.Error);
    }
}
