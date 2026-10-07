using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;

namespace ZenLead.Application.UseCases.Auth;

public class RegisterWorkspaceUseCase(
    IWorkspaceRepository workspaces,
    IIdentityService identity,
    IJwtTokenGenerator jwtTokenGenerator,
    IRefreshTokenService refreshTokens,
    IUnitOfWork unitOfWork)
{
    public async Task<AuthResponse> ExecuteAsync(RegisterRequest request, CancellationToken ct = default)
    {
        if (await identity.EmailExistsAsync(request.Email, ct))
            throw new EmailAlreadyRegisteredException();

        // workspace + user are created atomically so a failed user never leaves an orphan workspace
        var (workspaceId, userId) = await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var workspace = await workspaces.CreateAsync(request.WorkspaceName, ct);
            var userId = await identity.CreateUserAsync(workspace.Id, request.Email, request.Password, request.DisplayName, ct);
            return (workspace.Id, userId);
        }, ct);

        var accessToken = jwtTokenGenerator.GenerateAccessToken(userId, workspaceId, request.Email);
        var refreshToken = await refreshTokens.IssueAsync(userId, ct);

        return new AuthResponse(accessToken, refreshToken);
    }
}
