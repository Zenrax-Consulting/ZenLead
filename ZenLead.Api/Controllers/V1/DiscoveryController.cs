using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Dtos.Discovery;
using ZenLead.Application.UseCases.Discovery;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/discovery")]
public class DiscoveryController(IDiscoveryRunRepository runs, ILeadSource source, LeadSourceOptions options, TimeProvider clock) : ControllerBase
{
    private const int MaxRuns = 20;

    [HttpGet("runs/{id:guid}")]
    public async Task<ActionResult<DiscoveryRunResponse>> GetRun(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var run = await runs.GetAsync(id, ct);
        return run is null || run.WorkspaceId != workspaceId ? NotFound() : Ok(DiscoveryRunResponse.From(run));
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<DiscoveryRunResponse>>> ListRuns([FromQuery] Guid? targetProfileId, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var list = await runs.ListAsync(workspaceId, targetProfileId, MaxRuns, ct);
        return Ok(list.Select(DiscoveryRunResponse.From).ToList());
    }

    [HttpGet("credits")]
    public async Task<ActionResult<CreditsResponse>> Credits(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var used = await runs.GetCreditsUsedThisMonthAsync(workspaceId, StartDiscoveryRunUseCase.MonthStart(clock.GetUtcNow()), ct);

        int? providerRemaining = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            providerRemaining = await source.GetRemainingCreditsAsync(timeout.Token);
        }
        catch (LeadSourceException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        return Ok(new CreditsResponse(used, options.MonthlyCreditCap, Math.Max(0, options.MonthlyCreditCap - used), providerRemaining, options.MaxLeadsPerRun));
    }
}
