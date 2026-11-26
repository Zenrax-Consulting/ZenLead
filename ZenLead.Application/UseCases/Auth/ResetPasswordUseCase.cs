using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Email;
using ZenLead.Application.Security;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Auth;

/// <summary>Ok, a generic invalid token (unknown, used, expired — indistinguishable), or Identity policy errors.</summary>
public record ResetResult(bool IsOk, IReadOnlyList<string> Errors)
{
    public static readonly ResetResult Ok = new(true, []);
    public static readonly ResetResult InvalidToken = new(false, []);
    public static ResetResult WeakPassword(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>Sets a new password from a reset link (F21) and signs the user out everywhere.</summary>
public class ResetPasswordUseCase(
    IPasswordResetRepository tokens,
    IIdentityService identity,
    IRefreshTokenService refreshTokens,
    IEmailSender email,
    IOptions<AppOptions> app,
    TimeProvider clock,
    ILogger<ResetPasswordUseCase> logger)
{
    public async Task<ResetResult> ExecuteAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) return ResetResult.InvalidToken;
        var hash = TokenHasher.Hash(request.Token);
        var row = await tokens.FindByHashAsync(hash, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        // generic failure for: unknown, used, expired — the caller can't tell which
        if (row is null || row.UsedAt is not null || row.ExpiresAt <= now) return ResetResult.InvalidToken;

        var errors = await identity.ValidatePasswordAsync(row.UserId, request.NewPassword, ct);   // BEFORE consuming: typos don't burn the link
        if (errors.Count > 0) return ResetResult.WeakPassword(errors);

        if (!await tokens.TryConsumeAsync(hash, now, ct)) return ResetResult.InvalidToken;       // lost a race with another submit
        var user = await identity.FindByIdAsync(row.UserId, ct);
        if (user is null || user.Status != AppUserStatus.Active) return ResetResult.InvalidToken; // disabled since the email went out

        await identity.SetPasswordAsync(user.Id, request.NewPassword, ct);
        await refreshTokens.RevokeAllAsync(user.Id, ct);                                          // every session signed out
        await PasswordChangedNotice.SendAsync(email, app.Value, user, logger, ct);
        return ResetResult.Ok;
    }
}

/// <summary>The "your password was changed" email, shared by reset and change. Best effort: the change itself stands.</summary>
internal static class PasswordChangedNotice
{
    public static async Task SendAsync(IEmailSender email, AppOptions app, UserLookup user, ILogger logger, CancellationToken ct)
    {
        try
        {
            var result = await email.SendAsync(SystemEmail.Create(user.Email, user.DisplayName, "Your ZenLead password was changed",
                "The password for your account was just changed. If this wasn't you, reset it again now and tell your workspace administrator.",
                $"{app.PublicBaseUrl.TrimEnd('/')}/forgot-password", "Reset password"), ct);
            if (!result.Success) logger.LogWarning("Password-changed email for user {UserId} was not sent: {Error}", user.Id, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Password-changed email for user {UserId} failed.", user.Id);
        }
    }
}
