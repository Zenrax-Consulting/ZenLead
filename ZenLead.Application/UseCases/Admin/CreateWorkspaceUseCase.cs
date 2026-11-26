using ZenLead.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Application.Email;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Leads;

namespace ZenLead.Application.UseCases.Admin;

public record CreateWorkspaceResult(Workspace? Workspace)
{
    public bool IsDuplicate => Workspace is null;
    public static readonly CreateWorkspaceResult DuplicateName = new((Workspace?)null);
    public static CreateWorkspaceResult Created(Workspace workspace) => new(workspace);
}

/// <summary>The only way a workspace comes into existence (besides Phase 1 self-registration, which F20 removes).</summary>
public class CreateWorkspaceUseCase(
    IWorkspaceRepository workspaces,
    IAuditLog audit,
    IEmailSender email,
    IOptions<AppOptions> app,
    ILogger<CreateWorkspaceUseCase> logger)
{
    public const string AuditAction = "workspace.created";

    /// <remarks>The request must already have passed <c>CreateWorkspaceRequestValidator</c>.</remarks>
    public async Task<CreateWorkspaceResult> ExecuteAsync(Guid actorUserId, CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        if (await workspaces.NameExistsAsync(name, ct)) return CreateWorkspaceResult.DuplicateName;

        var adminEmail = LeadEmail.Normalize(request.AdminEmail)
            ?? throw new ArgumentException("Admin email is not valid; validate the request first.", nameof(request));

        Workspace workspace;
        try
        {
            workspace = await workspaces.CreateAsync(name, adminEmail, string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim(), ct);
        }
        catch (WorkspaceNameTakenException)
        {
            return CreateWorkspaceResult.DuplicateName;            // lost a race with a simultaneous create
        }

        await audit.WriteAsync(AuditAction, actorUserId, nameof(Workspace), workspace.Id, new { workspace.Name, AdminEmail = adminEmail }, ct);

        await SendWorkspaceReadyEmailAsync(workspace, adminEmail, ct);
        return CreateWorkspaceResult.Created(workspace);
    }

    /// <summary>Best effort: a mail failure is logged and never undoes the workspace (the admin can still register).</summary>
    private async Task SendWorkspaceReadyEmailAsync(Workspace workspace, string adminEmail, CancellationToken ct)
    {
        try
        {
            var registerUrl = $"{app.Value.PublicBaseUrl.TrimEnd('/')}/register";
            var result = await email.SendAsync(SystemEmail.Create(adminEmail, null,
                $"Your ZenLead workspace “{workspace.Name}” is ready",
                $"A workspace named “{workspace.Name}” has been created and you are its administrator. Register with this email address to get started.",
                registerUrl, "Register"), ct);
            if (!result.Success)
                logger.LogWarning("Workspace-ready email for workspace {WorkspaceId} was not sent: {Error}", workspace.Id, result.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Workspace-ready email for workspace {WorkspaceId} failed.", workspace.Id);
        }
    }
}
