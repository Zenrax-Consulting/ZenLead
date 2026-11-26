using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Email;
using ZenLead.Application.Security;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Auth;

public enum DecisionResult { Done, NotFound, AlreadyUsed, Expired }

/// <summary>What the approval page shows. Read-only: opening a link never changes state.</summary>
public class GetApprovalInfoUseCase(IApprovalRepository approvals, IIdentityService identity, IWorkspaceRepository workspaces, TimeProvider clock)
{
    public const string Pending = "pending", Used = "used", Expired = "expired";

    /// <returns>Null for an unknown token. Requester details only while the link is still usable.</returns>
    public async Task<ApprovalInfoResponse?> ExecuteAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var approval = await approvals.FindByHashAsync(TokenHasher.Hash(rawToken), ct);
        if (approval is null) return null;

        if (approval.UsedAt is not null) return new ApprovalInfoResponse(Used, null, null, null);
        if (approval.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return new ApprovalInfoResponse(Expired, null, null, null);

        var user = await identity.FindByIdAsync(approval.UserId, ct);
        if (user is null) return null;
        var workspace = await workspaces.GetByIdAsync(approval.WorkspaceId, ct);
        return new ApprovalInfoResponse(Pending, user.DisplayName, user.Email, workspace?.Name);
    }
}

public class DecideApprovalUseCase(
    IApprovalRepository approvals,
    IIdentityService identity,
    IRefreshTokenService refreshTokens,
    IEmailSender email,
    IOptions<AppOptions> app,
    TimeProvider clock,
    ILogger<DecideApprovalUseCase> logger)
{
    public async Task<DecisionResult> ExecuteAsync(string rawToken, ApprovalDecision decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return DecisionResult.NotFound;
        var hash = TokenHasher.Hash(rawToken);
        var approval = await approvals.FindByHashAsync(hash, ct);
        if (approval is null) return DecisionResult.NotFound;
        var now = clock.GetUtcNow().UtcDateTime;

        // atomic single use: two clicks or two link scanners → exactly one wins
        if (!await approvals.TryConsumeAsync(hash, now, decision, ct))
        {
            var current = await approvals.FindByHashAsync(hash, ct);           // re-read: the winner may have consumed it after our first read
            return current?.UsedAt is not null ? DecisionResult.AlreadyUsed : DecisionResult.Expired;
        }

        var user = await identity.FindByIdAsync(approval.UserId, ct);
        if (user is null || user.Status != AppUserStatus.PendingApproval) return DecisionResult.Done;   // e.g. disabled meanwhile: token burned, no change

        if (decision == ApprovalDecision.Approved)
        {
            await identity.SetStatusAsync(user.Id, AppUserStatus.Active, ct);
            await SendApprovedEmailAsync(user, ct);
        }
        else
        {
            // no email on rejection: the login message covers it, and we never email someone on a stranger's behalf
            await identity.SetStatusAsync(user.Id, AppUserStatus.Rejected, ct);
            await refreshTokens.RevokeAllAsync(user.Id, ct);                   // defensive; a pending user has none
        }
        return DecisionResult.Done;
    }

    private async Task SendApprovedEmailAsync(UserLookup user, CancellationToken ct)
    {
        try
        {
            var result = await email.SendAsync(SystemEmail.Create(user.Email, user.DisplayName, "You're approved on ZenLead",
                "Your request to join the workspace was approved. You can sign in now.",
                $"{app.Value.PublicBaseUrl.TrimEnd('/')}/login", "Sign in"), ct);
            if (!result.Success) logger.LogWarning("Approved email for user {UserId} was not sent: {Error}", user.Id, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Approved email for user {UserId} failed.", user.Id);   // the approval itself stands
        }
    }
}
