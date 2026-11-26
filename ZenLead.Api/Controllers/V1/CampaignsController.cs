using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Email;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Enums;

namespace ZenLead.Api.Controllers.V1;

/// <summary>Campaign builder API (F22). Nothing here sends mail; the sender is F23. Another workspace's campaign is always 404.</summary>
[ApiController]
[Authorize]
[Route("api/v1/campaigns")]
public class CampaignsController(
    CampaignsUseCase campaigns,
    CampaignStepsUseCase steps,
    EnrollLeadsUseCase enroll,
    ActivateCampaignUseCase activate,
    PauseCampaignUseCase pause,
    ActivationPreviewUseCase activationPreview,
    IOptions<EmailOptions> email,
    IValidator<CampaignRequest> campaignValidator,
    IValidator<StepRequest> stepValidator,
    IValidator<EnrollRequest> enrollValidator,
    IValidator<ReorderStepsRequest> reorderValidator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CampaignListItem>>> List(CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return Ok(await campaigns.ListAsync(workspaceId, ct));
    }

    [HttpPost]
    public async Task<ActionResult<CampaignResponse>> Create(CampaignRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId || this.UserId() is not { } userId) return Unauthorized();
        var validation = await campaignValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var created = await campaigns.CreateAsync(workspaceId, userId, request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CampaignResponse>> Get(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return ToResult(await campaigns.GetAsync(workspaceId, id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<CampaignResponse>> Update(Guid id, CampaignRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await campaignValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        return ToResult(await campaigns.UpdateAsync(workspaceId, id, request, ct));
    }

    [HttpPost("{id:guid}/steps")]
    public async Task<ActionResult<StepResponse>> AddStep(Guid id, StepRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await stepValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());

        var outcome = await steps.AddAsync(workspaceId, id, request, ct);
        return outcome.Kind == CampaignOutcomeKind.Ok ? CreatedAtAction(nameof(Get), new { id }, outcome.Value) : ToResult(outcome);
    }

    [HttpPut("{id:guid}/steps/{stepId:guid}")]
    public async Task<ActionResult<StepResponse>> UpdateStep(Guid id, Guid stepId, StepRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await stepValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        return ToResult(await steps.UpdateAsync(workspaceId, id, stepId, request, ct));
    }

    [HttpDelete("{id:guid}/steps/{stepId:guid}")]
    public async Task<IActionResult> DeleteStep(Guid id, Guid stepId, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var outcome = await steps.DeleteAsync(workspaceId, id, stepId, ct);
        return outcome.Kind == CampaignOutcomeKind.Ok ? NoContent() : ToResult(outcome).Result!;
    }

    [HttpPost("{id:guid}/steps/reorder")]
    public async Task<ActionResult<IReadOnlyList<StepResponse>>> ReorderSteps(Guid id, ReorderStepsRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await reorderValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        return ToResult(await steps.ReorderAsync(workspaceId, id, request, ct));
    }

    [HttpPost("{id:guid}/enroll")]
    public async Task<ActionResult<EnrollResult>> Enroll(Guid id, EnrollRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var validation = await enrollValidator.ValidateAsync(request, ct);
        if (!validation.IsValid) return ValidationProblem(validation.ToModelState());
        return ToResult(await enroll.ExecuteAsync(workspaceId, id, request, ct));
    }

    [HttpGet("{id:guid}/enrollments")]
    public async Task<ActionResult<PagedResult<EnrollmentRow>>> Enrollments(Guid id, [FromQuery] EnrollmentStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return ToResult(await campaigns.ListEnrollmentsAsync(workspaceId, id, status, page, pageSize, ct));
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<CampaignResponse>> Activate(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var outcome = await activate.ExecuteAsync(workspaceId, id, ct);
        return outcome.Kind == CampaignOutcomeKind.Ok ? ToResult(await campaigns.GetAsync(workspaceId, id, ct)) : ToResult(outcome).Result!;
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<ActionResult<CampaignResponse>> Pause(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        var outcome = await pause.ExecuteAsync(workspaceId, id, ct);
        return outcome.Kind == CampaignOutcomeKind.Ok ? ToResult(await campaigns.GetAsync(workspaceId, id, ct)) : ToResult(outcome).Result!;
    }

    /// <summary>The sending identity and whether its domain is verified (F24). Config only, no secrets.</summary>
    [HttpGet("sender")]
    public ActionResult<SenderStatus> Sender()
    {
        if (this.WorkspaceId() is null) return Unauthorized();
        var o = email.Value;
        return Ok(new SenderStatus(o.FromAddress, o.FromName, o.DomainVerified, o.InboundDomain));
    }

    /// <summary>AI call/cost estimate shown before activation (F24). Read-only.</summary>
    [HttpGet("{id:guid}/activation-preview")]
    public async Task<ActionResult<ActivationPreview>> ActivationPreview(Guid id, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return ToResult(await activationPreview.ExecuteAsync(workspaceId, id, ct));
    }

    [HttpPost("{id:guid}/preview")]
    public async Task<ActionResult<PreviewResponse>> Preview(Guid id, PreviewRequest request, CancellationToken ct)
    {
        if (this.WorkspaceId() is not { } workspaceId) return Unauthorized();
        return ToResult(await campaigns.PreviewAsync(workspaceId, id, request, ct));
    }

    private ActionResult<T> ToResult<T>(CampaignOutcome<T> outcome) => outcome.Kind switch
    {
        CampaignOutcomeKind.Ok => Ok(outcome.Value),
        CampaignOutcomeKind.NotFound => NotFound(),          // same whether missing or another workspace's — don't leak existence
        CampaignOutcomeKind.Invalid => ValidationProblem(detail: outcome.Message),
        _ => Conflict(new { code = outcome.Code, message = outcome.Message, problems = outcome.Problems ?? [] })
    };
}
