using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Auth;

public enum LoginOutcome { Success, InvalidCredentials, PendingApproval, NotApproved }

public record LoginResult(LoginOutcome Outcome, AuthResponse? Tokens = null);

public class LoginUseCase(
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator,
    IRefreshTokenService refreshTokens)
{
    public async Task<LoginResult> ExecuteAsync(LoginRequest request, CancellationToken ct = default)
    {
        var check = await identity.ValidateCredentialsAsync(request.Email, request.Password, ct);
        if (!check.Valid) return new LoginResult(LoginOutcome.InvalidCredentials);   // wrong password, unknown email, locked out: all the same

        switch (check.Status)
        {
            case AppUserStatus.Active: break;
            case AppUserStatus.PendingApproval: return new LoginResult(LoginOutcome.PendingApproval);
            default: return new LoginResult(LoginOutcome.NotApproved);                // Rejected / Disabled
        }

        var userId = check.UserId!.Value;
        var claimsData = await identity.GetUserClaimsDataAsync(userId, ct)
            ?? throw new InvalidOperationException("User not found after credential validation.");

        var accessToken = jwtTokenGenerator.GenerateAccessToken(userId, claimsData.WorkspaceId, claimsData.Email, claimsData.Role);
        var refreshToken = await refreshTokens.IssueAsync(userId, ct);

        return new LoginResult(LoginOutcome.Success, new AuthResponse(accessToken, refreshToken));
    }
}
