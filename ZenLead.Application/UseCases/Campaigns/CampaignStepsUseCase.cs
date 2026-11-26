using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Campaigns;

/// <summary>
/// Step editing with the running-campaign rules:
/// <list type="bullet">
/// <item>Add: appended as max+1; on Active/Paused it extends the plan for enrollments still in progress (finished ones stay finished).</item>
/// <item>Update: always allowed. A new <c>DelayDays</c> only affects enrollments not yet scheduled into that step; an already-stamped <c>NextSendAt</c> is kept.</item>
/// <item>Delete / reorder: refused (409 step_in_use) once an enrollment has passed the affected step. Draft is free.</item>
/// </list>
/// </summary>
public class CampaignStepsUseCase(ICampaignRepository campaigns, ICampaignEnrollmentRepository enrollments, IUnitOfWork unitOfWork)
{
    public async Task<CampaignOutcome<StepResponse>> AddAsync(Guid workspaceId, Guid campaignId, StepRequest request, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        if (campaign is null) return CampaignOutcome<StepResponse>.NotFound();
        if (campaign.Status == CampaignStatus.Completed)
            return CampaignOutcome<StepResponse>.Conflict(CampaignCodes.CampaignCompleted, "A completed campaign can't get new steps.");

        var step = new CampaignStep
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaignId,
            Order = campaign.Steps.Count == 0 ? 1 : campaign.Steps.Max(s => s.Order) + 1
        };
        Apply(step, request);
        await campaigns.AddStepAsync(step, ct);
        await campaigns.UpdateAsync(campaign, ct);
        return CampaignOutcome<StepResponse>.Success(CampaignsUseCase.ToResponse(step));
    }

    public async Task<CampaignOutcome<StepResponse>> UpdateAsync(Guid workspaceId, Guid campaignId, Guid stepId, StepRequest request, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        var step = campaign?.Steps.FirstOrDefault(s => s.Id == stepId);
        if (campaign is null || step is null) return CampaignOutcome<StepResponse>.NotFound();

        Apply(step, request);
        await campaigns.UpdateAsync(campaign, ct);
        return CampaignOutcome<StepResponse>.Success(CampaignsUseCase.ToResponse(step));
    }

    public async Task<CampaignOutcome<bool>> DeleteAsync(Guid workspaceId, Guid campaignId, Guid stepId, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        var step = campaign?.Steps.FirstOrDefault(s => s.Id == stepId);
        if (campaign is null || step is null) return CampaignOutcome<bool>.NotFound();

        if (campaign.Status != CampaignStatus.Draft && await enrollments.MaxNextStepOrderAsync(campaignId, ct) > step.Order)
            return CampaignOutcome<bool>.Conflict(CampaignCodes.StepInUse,
                $"Step {step.Order} can't be deleted: some leads have already been sent it. You can still edit its text.");

        var remaining = campaign.Steps.Where(s => s.Id != stepId).OrderBy(s => s.Order).ToList();
        if (remaining.Count > 0 && remaining[0].Order > step.Order) remaining[0].DelayDays = 0;   // the new first step sends immediately
        await campaigns.RemoveStepAsync(step, ct);
        campaign.Steps.Remove(step);
        await RenumberAsync(campaign, remaining, ct);
        return CampaignOutcome<bool>.Success(true);
    }

    public async Task<CampaignOutcome<IReadOnlyList<StepResponse>>> ReorderAsync(Guid workspaceId, Guid campaignId, ReorderStepsRequest request, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        if (campaign is null) return CampaignOutcome<IReadOnlyList<StepResponse>>.NotFound();

        var byId = campaign.Steps.ToDictionary(s => s.Id);
        if (request.OrderedStepIds.Count != byId.Count || request.OrderedStepIds.Distinct().Count() != byId.Count
            || request.OrderedStepIds.Any(id => !byId.ContainsKey(id)))
            return CampaignOutcome<IReadOnlyList<StepResponse>>.Invalid("orderedStepIds must list every step of the campaign exactly once.");

        if (campaign.Status != CampaignStatus.Draft && await enrollments.MaxNextStepOrderAsync(campaignId, ct) > 1)
            return CampaignOutcome<IReadOnlyList<StepResponse>>.Conflict(CampaignCodes.StepInUse,
                "Steps can't be reordered once leads have been sent the first step.");

        var ordered = request.OrderedStepIds.Select(id => byId[id]).ToList();
        await RenumberAsync(campaign, ordered, ct);
        return CampaignOutcome<IReadOnlyList<StepResponse>>.Success(ordered.Select(CampaignsUseCase.ToResponse).ToList());
    }

    private async Task<Campaign?> LoadAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
    {
        var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
        return campaign is null || campaign.WorkspaceId != workspaceId ? null : campaign;
    }

    /// <summary>
    /// Gives <paramref name="ordered"/> the orders 1..n. Two saves in one transaction, via temporary negative orders, so the
    /// unique (CampaignId, Order) index never sees two steps with the same order mid-update.
    /// </summary>
    private async Task RenumberAsync(Campaign campaign, IReadOnlyList<CampaignStep> ordered, CancellationToken ct)
    {
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            for (var i = 0; i < ordered.Count; i++) ordered[i].Order = -(i + 1);
            await campaigns.SaveAsync(ct);                     // also flushes a staged step removal
            for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i + 1;
            await campaigns.UpdateAsync(campaign, ct);
            return true;
        }, ct);
    }

    private static void Apply(CampaignStep step, StepRequest request)
    {
        step.DelayDays = request.DelayDays;
        step.SubjectTemplate = request.SubjectTemplate.Trim();
        step.BodyTemplate = request.BodyTemplate;
        step.UseAiPersonalisation = request.UseAiPersonalisation;
    }
}
