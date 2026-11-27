using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Dtos.Analytics;
using ZenLead.Application.UseCases.Analytics;

namespace ZenLead.Api.Controllers.V1;

/// <summary>
/// Workspace and per-campaign analytics (F28). <c>from</c>/<c>to</c> are local days (<c>yyyy-MM-dd</c>) in the workspace time zone,
/// both inclusive; default the last 30 days, at most 366. Another workspace's campaign is 404.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/analytics")]
public class AnalyticsController(AnalyticsUseCase analytics) : ControllerBase
{
    [HttpGet("workspace/summary")]
    public async Task<ActionResult<WorkspaceAnalytics>> Workspace([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        if (!ModelState.IsValid) return InvalidRange("'from' and 'to' must be dates in the form yyyy-MM-dd.");
        var outcome = await analytics.WorkspaceAsync(workspaceId, new AnalyticsQuery(from, to), ct);
        return outcome.Kind == AnalyticsOutcomeKind.Ok ? Ok(outcome.Value) : InvalidRange(outcome.Error);
    }

    [HttpGet("campaigns/{id:guid}/summary")]
    public async Task<ActionResult<CampaignAnalytics>> Campaign(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        if (!ModelState.IsValid) return InvalidRange("'from' and 'to' must be dates in the form yyyy-MM-dd.");
        var outcome = await analytics.CampaignAsync(workspaceId, id, new AnalyticsQuery(from, to), ct);
        return outcome.Kind switch
        {
            AnalyticsOutcomeKind.Ok => Ok(outcome.Value),
            AnalyticsOutcomeKind.NotFound => NotFound(),
            _ => InvalidRange(outcome.Error)
        };
    }

    private ObjectResult InvalidRange(string? detail)
        => Problem(title: "Invalid date range", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
