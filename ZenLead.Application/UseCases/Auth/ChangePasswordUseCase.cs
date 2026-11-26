using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public enum ChangePasswordOutcome { Success, WrongPassword, PolicyFailed }

public record ChangePasswordUseCaseResult(ChangePasswordOutcome Outcome, AuthResponse? Tokens = null, IReadOnlyList<string>? Errors = null);

/// <summary>
/// Signed-in password change (F21), for every user including the super admin. Revokes every session, then hands the caller
/// a fresh pair so they stay signed in on this device.
/// </summary>
public class ChangePasswordUseCase(
    IIdentityService identity,
    IRefreshTokenService refreshTokens,
    IJwtTokenGenerator jwt,
    IEmailSender email,
    IOptions<AppOptions> app,
    ILogger<ChangePasswordUseCase> logger)
{
    public async Task<ChangePasswordUseCaseResult> ExecuteAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var result = await identity.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, ct);
        switch (result)
        {
            case ChangePasswordResult.Success: break;
            case ChangePasswordResult.PolicyFailed failed: return new(ChangePasswordOutcome.PolicyFailed, Errors: failed.Errors);
            default: return new(ChangePasswordOutcome.WrongPassword);                    // wrong current or locked out: same answer
        }

        await refreshTokens.RevokeAllAsync(userId, ct);
        var claims = await identity.GetUserClaimsDataAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found after password change.");
        var tokens = new AuthResponse(
            jwt.GenerateAccessToken(userId, claims.WorkspaceId, claims.Email, claims.Role),
            await refreshTokens.IssueAsync(userId, ct));

        if (await identity.FindByIdAsync(userId, ct) is { } user)
            await PasswordChangedNotice.SendAsync(email, app.Value, user, logger, ct);
        return new(ChangePasswordOutcome.Success, tokens);
    }
}
