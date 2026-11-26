using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Workspaces;
using ZenLead.Domain.Scheduling;

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

    /// <summary>Sets the caller's workspace time zone (IANA id, e.g. "Asia/Dubai"); campaign send windows are evaluated in it.</summary>
    [HttpPut("current/time-zone")]
    public async Task<IActionResult> UpdateTimeZone(UpdateTimeZoneRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var timeZone = request.TimeZone?.Trim();
        if (!TimeZones.TryFind(timeZone, out _) || timeZone!.Length > 64)
        {
            ModelState.AddModelError(nameof(request.TimeZone), "Unknown time zone. Use an IANA id such as \"Europe/London\".");
            return ValidationProblem(ModelState);
        }
        return await workspaces.UpdateTimeZoneAsync(workspaceId, timeZone, ct) ? NoContent() : NotFound();
    }
}
