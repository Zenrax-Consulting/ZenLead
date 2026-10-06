using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

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

        var accessToken = jwtTokenGenerator.GenerateAccessToken(result.UserId, claimsData.WorkspaceId, claimsData.Email);
        return new AuthResponse(accessToken, result.NewRawToken!);
    }
}
