using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Sending;

namespace ZenLead.Application.UseCases.Campaigns;

/// <summary>
/// "This will make ~N AI calls, about $X" before activation (F24). Read-only; changes nothing.
/// </summary>
public class ActivationPreviewUseCase(
    ICampaignRepository campaigns,
    ICampaignEnrollmentRepository enrollments,
    AiPricing pricing,
    IAiBudget budget)
{
    /// <summary>
    /// Assumed size of one send-time personalisation call: about 600 prompt tokens (instructions + lead + template) and 300 completion
    /// tokens (subject + short body). An estimate for the confirm dialog only; real usage is logged per call in <c>AiUsageLog</c>.
    /// </summary>
    public const int AssumedPromptTokens = 600;
    public const int AssumedCompletionTokens = 300;

    public async Task<CampaignOutcome<ActivationPreview>> ExecuteAsync(Guid workspaceId, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
        if (campaign is null || campaign.WorkspaceId != workspaceId) return CampaignOutcome<ActivationPreview>.NotFound();

        var enrolledActive = (await enrollments.CountsAsync(campaignId, ct)).Active;
        var aiSteps = campaign.Steps.Count(s => s.UseAiPersonalisation);
        var calls = enrolledActive * aiSteps;
        var cost = calls * pricing.EstimateCostUsd(AssumedPromptTokens, AssumedCompletionTokens);
        var status = await budget.GetStatusAsync(workspaceId, ct);
        var remaining = Math.Max(status.CapUsd - status.SpentUsd, 0m);

        return CampaignOutcome<ActivationPreview>.Success(
            new ActivationPreview(enrolledActive, aiSteps, calls, Math.Round(cost, 4), Math.Round(remaining, 4)));
    }
}
