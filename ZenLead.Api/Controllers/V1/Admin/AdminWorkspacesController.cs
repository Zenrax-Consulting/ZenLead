using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Admin;
using ZenLead.Application.UseCases.Admin;

namespace ZenLead.Api.Controllers.V1.Admin;

[ApiController]
[Authorize(Policy = Policies.SuperAdminOnly)]
[Route("api/v1/admin/workspaces")]
public class AdminWorkspacesController(
    CreateWorkspaceUseCase create,
    IWorkspaceRepository workspaces,
    IValidator<CreateWorkspaceRequest> validator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkspaceAdminResponse>>> List(CancellationToken ct)
        => Ok(await workspaces.ListWithUserCountsAsync(ct));

    [HttpPost]
    public async Task<ActionResult<WorkspaceAdminResponse>> Create(CreateWorkspaceRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        if (this.UserId() is not { } actor) return Unauthorized();

        var result = await create.ExecuteAsync(actor, request, ct);
        if (result.IsDuplicate) return Conflict(new { message = "A workspace with that name already exists." });

        var w = result.Workspace!;
        return CreatedAtAction(nameof(List), new WorkspaceAdminResponse(w.Id, w.Name, w.AdminEmail, w.TimeZone, 0, w.CreatedAt));
    }
}
