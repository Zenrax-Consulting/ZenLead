using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;
using ZenLead.Domain.Scheduling;
using ZenLead.Domain.Templates;

namespace ZenLead.Application.UseCases.Campaigns;

/// <summary>
/// Enrolls leads (explicit ids or a lead-list filter) into a campaign. <c>DryRun</c> runs the identical checks and writes
/// nothing, so a confirm dialog shows exactly what the real call will do.
/// </summary>
public class EnrollLeadsUseCase(
    ICampaignRepository campaigns,
    ICampaignEnrollmentRepository enrollments,
    ILeadRepository leads,
    IWorkspaceRepository workspaces,
    TimeProvider clock,
    ILogger<EnrollLeadsUseCase>? logger = null)
{
    public const int MaxPerRequest = 5000;

    private readonly ILogger _logger = logger ?? NullLogger<EnrollLeadsUseCase>.Instance;

    public async Task<CampaignOutcome<EnrollResult>> ExecuteAsync(Guid workspaceId, Guid campaignId, EnrollRequest request, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
        if (campaign is null || campaign.WorkspaceId != workspaceId) return CampaignOutcome<EnrollResult>.NotFound();
        if (campaign.Status == CampaignStatus.Completed)
            return CampaignOutcome<EnrollResult>.Conflict(CampaignCodes.CampaignCompleted, "This campaign has completed; leads can't be added to it.");

        var ids = request.LeadIds ?? await leads.ListIdsAsync(workspaceId, request.Filter!, MaxPerRequest + 1, ct);
        var distinct = ids.Distinct().ToList();
        var truncated = distinct.Count > MaxPerRequest;
        distinct = distinct.Take(MaxPerRequest).ToList();

        var found = await leads.GetByIdsAsync(workspaceId, distinct, ct);   // other tenants' / deleted ids simply don't resolve
        var requiredTokens = campaign.Steps
            .SelectMany(s => TemplateRenderer.RequiredTokens(s.SubjectTemplate).Concat(TemplateRenderer.RequiredTokens(s.BodyTemplate)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var window = CampaignWindowFactory.For(campaign, await workspaces.WorkspaceTimeZoneAsync(workspaceId, ct), _logger);
        var now = clock.GetUtcNow().UtcDateTime;
        DateTime? nextSendAt = campaign.Status == CampaignStatus.Draft ? null : SendScheduler.NextSendAt(now, 0, window);

        for (var attempt = 1; ; attempt++)
        {
            var already = await enrollments.ExistingLeadIdsAsync(campaignId, found.Select(l => l.Id).ToList(), ct);
            var (result, toAdd) = Plan(workspaceId, campaignId, found, already, requiredTokens, nextSendAt, now);
            result = result with { NotFound = distinct.Count - found.Count, Truncated = truncated };
            if (request.DryRun || toAdd.Count == 0) return CampaignOutcome<EnrollResult>.Success(result);

            try
            {
                await enrollments.AddRangeAsync(toAdd, ct);   // unique (CampaignId, LeadId) is the backstop against a double click
                return CampaignOutcome<EnrollResult>.Success(result);
            }
            catch (DuplicateEnrollmentException) when (attempt == 1)
            {
                // a concurrent submit won the race for some leads: re-read who is enrolled and try once more
            }
        }
    }

    private static (EnrollResult Result, List<CampaignEnrollment> ToAdd) Plan(
        Guid workspaceId, Guid campaignId, IReadOnlyList<Lead> found, IReadOnlySet<Guid> already,
        IReadOnlyList<string> requiredTokens, DateTime? nextSendAt, DateTime now)
    {
        int enrolled = 0, alreadyEnrolled = 0, suppressed = 0, invalidEmail = 0, missingTokens = 0, warnUnverified = 0, warnRisky = 0;
        var toAdd = new List<CampaignEnrollment>();

        foreach (var lead in found)
        {
            if (already.Contains(lead.Id)) { alreadyEnrolled++; continue; }

            var eligibility = EnrollmentEligibility.Check(lead.Status, lead.EmailVerificationStatus);
            if (eligibility.Kind == EligibilityKind.Blocked)
            {
                if (lead.EmailVerificationStatus == EmailVerificationStatus.Invalid) invalidEmail++; else suppressed++;
                continue;
            }

            var values = TemplateRenderer.ValuesFor(lead);
            if (requiredTokens.Any(t => string.IsNullOrWhiteSpace(values.GetValueOrDefault(t)))) { missingTokens++; continue; }

            if (eligibility.Kind == EligibilityKind.EligibleWithWarning)
            {
                if (lead.EmailVerificationStatus == EmailVerificationStatus.Risky) warnRisky++; else warnUnverified++;
            }

            enrolled++;
            toAdd.Add(new CampaignEnrollment
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaignId, LeadId = lead.Id,
                NextStepOrder = 1, NextSendAt = nextSendAt, Status = EnrollmentStatus.Active, EnrolledAt = now
            });
        }

        return (new EnrollResult(enrolled, alreadyEnrolled, suppressed, invalidEmail, missingTokens, 0, warnUnverified, warnRisky, false), toAdd);
    }
}
