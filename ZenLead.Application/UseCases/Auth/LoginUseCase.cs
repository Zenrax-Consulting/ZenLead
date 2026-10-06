using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public class LoginUseCase(
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator,
    IRefreshTokenService refreshTokens)
{
    public async Task<AuthResponse?> ExecuteAsync(LoginRequest request, CancellationToken ct = default)
    {
        var userId = await identity.ValidateCredentialsAsync(request.Email, request.Password, ct);
        if (userId is null) return null;

        var claimsData = await identity.GetUserClaimsDataAsync(userId.Value, ct)
            ?? throw new InvalidOperationException("User not found after credential validation.");

        var accessToken = jwtTokenGenerator.GenerateAccessToken(userId.Value, claimsData.WorkspaceId, claimsData.Email);
        var refreshToken = await refreshTokens.IssueAsync(userId.Value, ct);

        return new AuthResponse(accessToken, refreshToken);
    }
}
