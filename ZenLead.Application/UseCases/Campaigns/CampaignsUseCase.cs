using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Validation.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Templates;

namespace ZenLead.Application.UseCases.Campaigns;

/// <summary>Campaign create/read/update, the enrollment list and the template preview. Requests are validated by the caller.</summary>
public class CampaignsUseCase(
    ICampaignRepository campaigns,
    ICampaignEnrollmentRepository enrollments,
    ILeadRepository leads,
    IWorkspaceRepository workspaces,
    TimeProvider clock)
{
    public Task<IReadOnlyList<CampaignListItem>> ListAsync(Guid workspaceId, CancellationToken ct = default)
        => campaigns.ListWithCountsAsync(workspaceId, ct);

    public async Task<CampaignResponse> CreateAsync(Guid workspaceId, Guid userId, CampaignRequest request, CancellationToken ct = default)
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Status = CampaignStatus.Draft,
            CreatedBy = userId, CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        Apply(campaign, request);
        await campaigns.AddAsync(campaign, ct);
        return await ToResponseAsync(campaign, EnrollmentCounts.Empty, ct);
    }

    public async Task<CampaignOutcome<CampaignResponse>> GetAsync(Guid workspaceId, Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        if (campaign is null) return CampaignOutcome<CampaignResponse>.NotFound();
        return CampaignOutcome<CampaignResponse>.Success(await ToResponseAsync(campaign, await enrollments.CountsAsync(campaignId, ct), ct));
    }

    /// <summary>Allowed in any status but Completed; window changes apply to future scheduling only.</summary>
    public async Task<CampaignOutcome<CampaignResponse>> UpdateAsync(Guid workspaceId, Guid campaignId, CampaignRequest request, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        if (campaign is null) return CampaignOutcome<CampaignResponse>.NotFound();
        if (campaign.Status == CampaignStatus.Completed)
            return CampaignOutcome<CampaignResponse>.Conflict(CampaignCodes.CampaignCompleted, "A completed campaign can't be edited.");

        Apply(campaign, request);
        await campaigns.UpdateAsync(campaign, ct);
        return CampaignOutcome<CampaignResponse>.Success(await ToResponseAsync(campaign, await enrollments.CountsAsync(campaignId, ct), ct));
    }

    public async Task<CampaignOutcome<PagedResult<EnrollmentRow>>> ListEnrollmentsAsync(
        Guid workspaceId, Guid campaignId, EnrollmentStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        if (campaign is null) return CampaignOutcome<PagedResult<EnrollmentRow>>.NotFound();

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await enrollments.ListAsync(campaignId, status, page, pageSize, ct);
        var rows = result.Items.Select(e => new EnrollmentRow(e.Id, e.LeadId, e.Lead?.Name ?? string.Empty, e.Lead?.Email ?? string.Empty,
            e.NextStepOrder, e.NextSendAt, e.Status, e.FailureReason)).ToList();
        return CampaignOutcome<PagedResult<EnrollmentRow>>.Success(new PagedResult<EnrollmentRow>(rows, result.Total, result.Page, result.PageSize));
    }

    /// <summary>Renders a step for one lead exactly as the sender will (the unsubscribe link is a placeholder).</summary>
    public async Task<CampaignOutcome<PreviewResponse>> PreviewAsync(Guid workspaceId, Guid campaignId, PreviewRequest request, CancellationToken ct = default)
    {
        var campaign = await LoadAsync(workspaceId, campaignId, ct);
        var step = campaign?.Steps.FirstOrDefault(s => s.Id == request.StepId);
        if (campaign is null || step is null) return CampaignOutcome<PreviewResponse>.NotFound();
        var lead = (await leads.GetByIdsAsync(workspaceId, [request.LeadId], ct)).FirstOrDefault();
        if (lead is null) return CampaignOutcome<PreviewResponse>.NotFound();

        var values = TemplateRenderer.ValuesFor(lead);
        var subject = TemplateRenderer.Render(step.SubjectTemplate, values);
        var body = TemplateRenderer.Render(step.BodyTemplate, values);
        var missing = subject.MissingTokens.Concat(body.MissingTokens).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return CampaignOutcome<PreviewResponse>.Success(
            new PreviewResponse(subject.Text, UnsubscribeFooter.Append(body.Text, UnsubscribeFooter.PreviewUrl), missing));
    }

    private async Task<Campaign?> LoadAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
    {
        var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
        return campaign is null || campaign.WorkspaceId != workspaceId ? null : campaign;
    }

    private static void Apply(Campaign campaign, CampaignRequest request)
    {
        campaign.Name = request.Name.Trim();
        campaign.FromName = string.IsNullOrWhiteSpace(request.FromName) ? null : request.FromName.Trim();
        campaign.DailySendCap = request.DailySendCap;
        CampaignTimes.TryParse(request.WindowStart, out var start);
        CampaignTimes.TryParse(request.WindowEnd, out var end);
        campaign.WindowStart = start;
        campaign.WindowEnd = end;
        campaign.SendDays = request.SendDays;
        campaign.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? null : request.TimeZoneId.Trim();
    }

    private async Task<CampaignResponse> ToResponseAsync(Campaign c, EnrollmentCounts counts, CancellationToken ct)
    {
        var zone = CampaignWindowFactory.EffectiveTimeZone(c, await workspaces.WorkspaceTimeZoneAsync(c.WorkspaceId, ct));
        return new CampaignResponse(c.Id, c.Name, c.Status, c.FromName, c.DailySendCap, CampaignTimes.ToText(c.WindowStart), CampaignTimes.ToText(c.WindowEnd),
            c.SendDays, c.TimeZoneId, zone, c.CreatedAt, c.ActivatedAt, c.Steps.OrderBy(s => s.Order).Select(ToResponse).ToList(), counts);
    }

    public static StepResponse ToResponse(CampaignStep s)
        => new(s.Id, s.Order, s.DelayDays, s.SubjectTemplate, s.BodyTemplate, s.UseAiPersonalisation);
}
