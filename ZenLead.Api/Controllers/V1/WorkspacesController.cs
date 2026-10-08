using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Workspaces;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/workspaces")]
public class WorkspacesController(IWorkspaceRepository workspaces) : ControllerBase
{
    [HttpGet("current")]
    public async Task<ActionResult<CurrentWorkspaceResponse>> Current(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var workspace = await workspaces.GetByIdAsync(workspaceId, ct);
        return workspace is null ? NotFound() : Ok(new CurrentWorkspaceResponse(workspace.Id, workspace.Name));
    }
}
