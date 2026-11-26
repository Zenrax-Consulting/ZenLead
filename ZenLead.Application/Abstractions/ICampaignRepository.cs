using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.Abstractions;

public interface ICampaignRepository
{
    Task AddAsync(Campaign campaign, CancellationToken ct = default);

    /// <summary>Tracked, with <see cref="Campaign.Steps"/> loaded (ordered by <c>Order</c>). Other workspaces' campaigns don't resolve.</summary>
    Task<Campaign?> GetWithStepsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first, with step and enrollment counts in one query (no N+1).</summary>
    Task<IReadOnlyList<CampaignListItem>> ListWithCountsAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>Stamps <see cref="Campaign.UpdatedAt"/> and saves the tracked campaign (and any staged step changes).</summary>
    Task UpdateAsync(Campaign campaign, CancellationToken ct = default);

    /// <summary>Stages a new step; persisted by the next <see cref="SaveAsync"/> / <see cref="UpdateAsync"/>.</summary>
    Task AddStepAsync(CampaignStep step, CancellationToken ct = default);

    /// <summary>Stages a step removal; persisted by the next <see cref="SaveAsync"/> / <see cref="UpdateAsync"/>.</summary>
    Task RemoveStepAsync(CampaignStep step, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}

public interface ICampaignEnrollmentRepository
{
    /// <summary>Which of <paramref name="leadIds"/> are already enrolled in the campaign.</summary>
    Task<IReadOnlySet<Guid>> ExistingLeadIdsAsync(Guid campaignId, IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default);

    /// <exception cref="DuplicateEnrollmentException">A concurrent request enrolled one of the leads first (unique (CampaignId, LeadId)).</exception>
    Task AddRangeAsync(IReadOnlyCollection<CampaignEnrollment> enrollments, CancellationToken ct = default);

    /// <summary>The highest <see cref="CampaignEnrollment.NextStepOrder"/> of any enrollment (0 when none): "has anyone passed step k" is <c>max &gt; k</c>.</summary>
    Task<int> MaxNextStepOrderAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Sets <c>NextSendAt</c> for Active enrollments that have none yet (pages of 1000). Returns how many were stamped.</summary>
    Task<int> StampNextSendAtAsync(Guid campaignId, SendWindow window, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Enrollments with their <see cref="CampaignEnrollment.Lead"/> (soft-deleted leads included: the history stays).</summary>
    Task<PagedResult<CampaignEnrollment>> ListAsync(Guid campaignId, EnrollmentStatus? status, int page, int pageSize, CancellationToken ct = default);

    Task<EnrollmentCounts> CountsAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Applies <see cref="EnrollmentRules.ApplyStop"/> to every live enrollment of the lead, in every campaign of the workspace
    /// (explicitly scoped: works without a current workspace). Staged, not saved. Returns how many changed.
    /// </summary>
    Task<int> StopAllForLeadAsync(Guid workspaceId, Guid leadId, EnrollmentRules.StopEvent stop, CancellationToken ct = default);
}

public class DuplicateEnrollmentException() : InvalidOperationException("One of the leads was enrolled in this campaign by another request.");
