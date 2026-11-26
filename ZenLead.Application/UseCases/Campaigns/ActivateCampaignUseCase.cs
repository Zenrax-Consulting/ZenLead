using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Email;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Templates;

namespace ZenLead.Application.UseCases.Campaigns;

/// <summary>Draft/Paused → Active after validation; stamps <c>NextSendAt</c> on enrollments that have none yet.</summary>
public class ActivateCampaignUseCase(
    ICampaignRepository campaigns,
    ICampaignEnrollmentRepository enrollments,
    IWorkspaceRepository workspaces,
    IUnitOfWork unitOfWork,
    IOptions<EmailOptions> email,
    TimeProvider clock,
    ILogger<ActivateCampaignUseCase>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ActivateCampaignUseCase>.Instance;

    public static IReadOnlyList<ActivationProblem> Validate(Campaign c, EmailOptions email)
    {
        var problems = new List<ActivationProblem>();
        if (c.Steps.Count == 0) problems.Add(new("no_steps", "Add at least one step."));
        var ordered = c.Steps.OrderBy(s => s.Order).ToList();
        if (ordered.Select((s, i) => s.Order == i + 1).Any(ok => !ok)) problems.Add(new("steps_unordered", "Step order must be 1, 2, 3…"));
        if (ordered.FirstOrDefault() is { DelayDays: not 0 }) problems.Add(new("first_delay", "The first step must send immediately (delay 0).", ordered[0].Order));
        foreach (var s in ordered)
        {
            if (string.IsNullOrWhiteSpace(s.SubjectTemplate)) problems.Add(new("empty_subject", $"Step {s.Order} has no subject.", s.Order));
            if (string.IsNullOrWhiteSpace(s.BodyTemplate)) problems.Add(new("empty_body", $"Step {s.Order} has no body.", s.Order));
            foreach (var t in TemplateRenderer.UnknownTokens(s.SubjectTemplate).Concat(TemplateRenderer.UnknownTokens(s.BodyTemplate))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                problems.Add(new("bad_token", $"Step {s.Order} uses unknown token {{{{{t}}}}}.", s.Order));
        }
        if (c.WindowStart >= c.WindowEnd || c.SendDays == SendDays.None) problems.Add(new("window_invalid", "Send window is invalid."));
        if (!email.DomainVerified) problems.Add(new("domain_unverified", "The sending domain isn't verified yet."));
        return problems;
    }

    public async Task<CampaignOutcome<CampaignStatus>> ExecuteAsync(Guid workspaceId, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
        if (campaign is null || campaign.WorkspaceId != workspaceId) return CampaignOutcome<CampaignStatus>.NotFound();

        switch (campaign.Status)
        {
            case CampaignStatus.Active:
                return CampaignOutcome<CampaignStatus>.Success(CampaignStatus.Active);       // idempotent
            case CampaignStatus.Completed:
                return CampaignOutcome<CampaignStatus>.Conflict(CampaignCodes.CampaignCompleted, "A completed campaign can't be activated again.");
        }

        var problems = Validate(campaign, email.Value);
        if (problems.Count > 0)
            return CampaignOutcome<CampaignStatus>.Conflict(CampaignCodes.ActivationBlocked, "The campaign can't be activated yet.", problems);

        var window = CampaignWindowFactory.For(campaign, await workspaces.WorkspaceTimeZoneAsync(workspaceId, ct), _logger);
        var now = clock.GetUtcNow().UtcDateTime;

        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            campaign.Status = CampaignStatus.Active;
            campaign.ActivatedAt ??= now;
            await campaigns.UpdateAsync(campaign, ct);
            return await enrollments.StampNextSendAtAsync(campaignId, window, now, ct);
        }, ct);

        return CampaignOutcome<CampaignStatus>.Success(CampaignStatus.Active);
    }
}

/// <summary>Active → Paused. Enrollments are not rewritten: the sender only picks Active campaigns, so their NextSendAt simply ages.</summary>
public class PauseCampaignUseCase(ICampaignRepository campaigns)
{
    public async Task<CampaignOutcome<CampaignStatus>> ExecuteAsync(Guid workspaceId, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
        if (campaign is null || campaign.WorkspaceId != workspaceId) return CampaignOutcome<CampaignStatus>.NotFound();

        switch (campaign.Status)
        {
            case CampaignStatus.Paused:
                return CampaignOutcome<CampaignStatus>.Success(CampaignStatus.Paused);       // idempotent
            case CampaignStatus.Active:
                campaign.Status = CampaignStatus.Paused;
                await campaigns.UpdateAsync(campaign, ct);
                return CampaignOutcome<CampaignStatus>.Success(CampaignStatus.Paused);
            default:
                return CampaignOutcome<CampaignStatus>.Conflict(CampaignCodes.InvalidTransition, $"Only an active campaign can be paused (this one is {campaign.Status}).");
        }
    }
}
