using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Auth;

public class RefreshTokenUseCase(
    IRefreshTokenService refreshTokens,
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator)
{
    public async Task<AuthResponse?> ExecuteAsync(RefreshRequest request, CancellationToken ct = default)
    {
        var result = await refreshTokens.ValidateAndRotateAsync(request.RefreshToken, ct);
        if (!result.Succeeded) return null;

        var claimsData = await identity.GetUserClaimsDataAsync(result.UserId, ct)
            ?? throw new InvalidOperationException("User not found for valid refresh token.");

        // a user who was disabled (or otherwise stopped being Active) loses every session at the next refresh
        if (claimsData.Status != AppUserStatus.Active)
        {
            await refreshTokens.RevokeAllAsync(result.UserId, ct);
            return null;
        }

        var accessToken = jwtTokenGenerator.GenerateAccessToken(result.UserId, claimsData.WorkspaceId, claimsData.Email, claimsData.Role); // super admin keeps its role and stays workspace-less
        return new AuthResponse(accessToken, result.NewRawToken!);
    }
}
