using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Auth;
using ZenLead.Application.Dtos.Workspaces;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Route("api/v1/workspaces")]
public class WorkspacesController(IWorkspaceRepository workspaces) : ControllerBase
{
    public const int MaxSearchResults = 20;

    /// <summary>
    /// Registration picker (F20): workspace ids and names only, never the admin email. Anonymous exposure of names is accepted while
    /// Zenrax is the only tenant; revisit before onboarding other organisations.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiting.PickerPolicy)]
    public async Task<ActionResult<IReadOnlyList<WorkspaceOption>>> Search([FromQuery] string? q, CancellationToken ct)
        => Ok(await workspaces.SearchByNameAsync(q?.Trim() is { Length: <= 100 } s ? s : null, MaxSearchResults, ct));

    [HttpGet("current")]
    [Authorize]
    public async Task<ActionResult<CurrentWorkspaceResponse>> Current(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var workspace = await workspaces.GetByIdAsync(workspaceId, ct);
        return workspace is null ? NotFound() : Ok(new CurrentWorkspaceResponse(workspace.Id, workspace.Name));
    }

    /// <summary>Sets the caller's workspace time zone (IANA id, e.g. "Asia/Dubai"); campaign send windows are evaluated in it.</summary>
    [HttpPut("current/time-zone")]
    [Authorize]
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
