using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Dtos.Discovery;
using ZenLead.Application.UseCases.Discovery;
using ZenLead.Domain.Discovery;
using ZenLead.Domain.Entities;

namespace ZenLead.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/target-profiles")]
public class TargetProfilesController(
    ITargetProfileRepository profiles,
    ILeadRepository leads,
    StartDiscoveryRunUseCase startRun,
    IValidator<TargetProfileRequest> profileValidator,
    IValidator<SuggestFromLeadsRequest> suggestValidator,
    IValidator<StartRunRequest> startValidator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TargetProfileResponse>>> List(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var all = await profiles.ListAsync(ct);
        return Ok(all.Where(p => p.WorkspaceId == workspaceId).Select(ToResponse).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<TargetProfileResponse>> Create(TargetProfileRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        if (this.UserId() is not { } userId) return Unauthorized();
        var validation = await profileValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var profile = await profiles.AddAsync(new TargetProfile
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = request.Name.Trim(),
            CriteriaJson = LeadCriteriaJson.Serialize(LeadCriteriaJson.ToCriteria(request.Criteria)),
            CreatedBy = userId, CreatedAt = DateTime.UtcNow
        }, ct);
        return CreatedAtAction(nameof(List), ToResponse(profile));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TargetProfileResponse>> Update(Guid id, TargetProfileRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var profile = await profiles.GetAsync(id, ct);
        if (profile is null || profile.WorkspaceId != workspaceId) return NotFound();
        var validation = await profileValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        profile.Name = request.Name.Trim();
        profile.CriteriaJson = LeadCriteriaJson.Serialize(LeadCriteriaJson.ToCriteria(request.Criteria));
        await profiles.UpdateAsync(profile, ct);
        return Ok(ToResponse(profile));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var profile = await profiles.GetAsync(id, ct);
        if (profile is null || profile.WorkspaceId != workspaceId) return NotFound();
        await profiles.DeleteAsync(profile, ct);
        return NoContent();
    }

    /// <summary>Suggests a profile from existing leads. Nothing is saved.</summary>
    [HttpPost("from-leads")]
    public async Task<ActionResult<SuggestedProfileResponse>> FromLeads(SuggestFromLeadsRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await suggestValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var found = await leads.GetByIdsAsync(workspaceId, request.LeadIds.Distinct().ToList(), ct);   // other tenants' ids simply don't resolve
        if (found.Count == 0) return NotFound();

        var inputs = found.Select(l => new LeadProfileInput(l.Title, l.Company?.Industry, l.Company?.Country, l.Company?.Size, l.Status)).ToList();
        if (request.OnlyReplied && !inputs.Any(i => i.Status == Domain.Enums.LeadStatus.Replied))
            return BadRequest(new { message = "None of the selected leads have replied." });

        var single = found.Count == 1 ? found[0] : null;
        var suggestion = TargetProfileSuggester.Suggest(inputs, single?.Name, request.OnlyReplied);
        var criteria = new LeadSearchCriteria(suggestion.JobTitles, suggestion.Industries, suggestion.Countries, suggestion.SizeMin, suggestion.SizeMax, []);
        return Ok(new SuggestedProfileResponse(suggestion.Name, LeadCriteriaJson.ToDto(criteria), single?.Id));
    }

    [HttpPost("{id:guid}/runs")]
    [EnableRateLimiting(RateLimiting.DiscoveryPolicy)]
    public async Task<ActionResult<DiscoveryRunResponse>> StartRun(Guid id, StartRunRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        if (this.UserId() is not { } userId) return Unauthorized();
        var validation = await startValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var result = await startRun.ExecuteAsync(workspaceId, userId, id, request.MaxLeads, ct);
        switch (result.Outcome)
        {
            case StartRunOutcome.NotFound: return NotFound();
            case StartRunOutcome.AlreadyRunning: return Conflict(new { message = "A discovery run is already in progress." });
            case StartRunOutcome.CapReached: return Conflict(new { message = "Monthly discovery credit cap reached." });
            default:
                var response = DiscoveryRunResponse.From(result.Run!);
                return Accepted($"/api/v1/discovery/runs/{response.Id}", response);
        }
    }

    private static TargetProfileResponse ToResponse(TargetProfile p)
        => new(p.Id, p.Name, LeadCriteriaJson.ToDto(LeadCriteriaJson.Deserialize(p.CriteriaJson)), p.SourceLeadId, p.CreatedAt);
}
