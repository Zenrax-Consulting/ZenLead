using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public class RegisterWorkspaceUseCase(
    IWorkspaceRepository workspaces,
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator,
    IRefreshTokenService refreshTokens)
{
    public async Task<AuthResponse> ExecuteAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await identity.EmailExistsAsync(request.Email, ct))
            throw new InvalidOperationException("Email already registered.");

        var workspace = await workspaces.CreateAsync(request.WorkspaceName, ct);
        var userId = await identity.CreateUserAsync(workspace.Id, request.Email, request.Password, request.DisplayName, ct);

        var accessToken = jwtTokenGenerator.GenerateAccessToken(userId, workspace.Id, request.Email);
        var refreshToken = await refreshTokens.IssueAsync(userId, ct);

        return new AuthResponse(accessToken, refreshToken);
    }
}
