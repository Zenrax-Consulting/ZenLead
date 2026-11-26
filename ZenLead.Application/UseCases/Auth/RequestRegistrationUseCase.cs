using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Auth;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Email;
using ZenLead.Application.Security;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Auth;

public enum RegistrationOutcome { Accepted, UnknownWorkspace, WorkspaceNotAccepting, TooManyPending }

/// <summary>
/// Registration no longer creates a usable account (F20): the user joins an existing workspace as <see cref="AppUserStatus.PendingApproval"/>
/// and the workspace's admin email gets a single-use approval link. The answer is the same whether the address is new or already known.
/// </summary>
public class RequestRegistrationUseCase(
    IWorkspaceRepository workspaces,
    IIdentityService identity,
    IApprovalRepository approvals,
    IEmailSender email,
    IOptions<AppOptions> app,
    IOptions<RegistrationOptions> options,
    TimeProvider clock,
    ILogger<RequestRegistrationUseCase> logger)
{
    /// <remarks>The request must already have passed <c>RegisterRequestValidator</c>.</remarks>
    /// <exception cref="RegistrationFailedException">Identity rejected the password.</exception>
    public async Task<RegistrationOutcome> ExecuteAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var workspace = await workspaces.GetByIdAsync(request.WorkspaceId, ct);
        if (workspace is null) return RegistrationOutcome.UnknownWorkspace;
        if (string.IsNullOrWhiteSpace(workspace.AdminEmail)) return RegistrationOutcome.WorkspaceNotAccepting;

        var now = clock.GetUtcNow().UtcDateTime;
        var emailAddress = request.Email.Trim();
        var displayName = request.DisplayName.Trim();

        var existing = await identity.FindByEmailAsync(emailAddress, ct);
        if (existing is not null)
        {
            identity.BurnPasswordHash();                                       // same cost as the create path
            await ResendIfDueAsync(existing, workspace, now, ct);
            return RegistrationOutcome.Accepted;                               // identical response in every "exists" case
        }

        if (await identity.CountPendingAsync(workspace.Id, ct) >= options.Value.MaxPendingPerWorkspace)
            return RegistrationOutcome.TooManyPending;

        Guid userId;
        try
        {
            userId = await identity.CreateUserAsync(workspace.Id, emailAddress, request.Password, displayName, AppUserStatus.PendingApproval, ct);
        }
        catch (EmailAlreadyRegisteredException)
        {
            return RegistrationOutcome.Accepted;                               // lost a race on the same address: still the "exists" answer
        }

        await IssueAndSendAsync(userId, displayName, emailAddress, workspace, now, ct);
        return RegistrationOutcome.Accepted;
    }

    /// <summary>A still-pending user asking again for the same workspace gets a fresh link, at most once per <see cref="RegistrationOptions.ResendAfter"/>.</summary>
    private async Task ResendIfDueAsync(UserLookup existing, Workspace workspace, DateTime now, CancellationToken ct)
    {
        if (existing.Status != AppUserStatus.PendingApproval || existing.WorkspaceId != workspace.Id) return;
        var last = await approvals.LastCreatedAtAsync(existing.Id, ct);
        if (last is { } l && l > now - options.Value.ResendAfter) return;
        await IssueAndSendAsync(existing.Id, existing.DisplayName, existing.Email, workspace, now, ct);
    }

    private async Task IssueAndSendAsync(Guid userId, string name, string requesterEmail, Workspace workspace, DateTime now, CancellationToken ct)
    {
        await approvals.InvalidatePendingForUserAsync(userId, now, ct);        // only the newest link works
        var raw = TokenHasher.NewUrlSafeToken();
        await approvals.AddAsync(new RegistrationApproval
        {
            Id = Guid.NewGuid(), UserId = userId, WorkspaceId = workspace.Id, TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now, ExpiresAt = now.AddDays(options.Value.ApprovalLifetimeDays)
        }, ct);

        var link = $"{app.Value.PublicBaseUrl.TrimEnd('/')}/approve?token={raw}";
        var message = SystemEmail.Create(workspace.AdminEmail!, null,
            $"{name} wants to join {workspace.Name} on ZenLead",
            $"{name} ({requesterEmail}) has asked to join the “{workspace.Name}” workspace.\n"
            + $"Only approve people you recognise. The link is valid for {options.Value.ApprovalLifetimeDays} days and can be used once.",
            link, "Review request");

        // a failed send never changes the answer (no enumeration through errors); the user can ask again after ResendAfter
        try
        {
            var result = await email.SendAsync(message, ct);
            if (!result.Success) logger.LogWarning("Approval email for user {UserId} was not sent: {Error}", userId, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Approval email for user {UserId} failed.", userId);
        }
    }
}
