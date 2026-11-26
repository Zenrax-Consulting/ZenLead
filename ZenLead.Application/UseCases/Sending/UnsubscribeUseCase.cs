using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;
using static ZenLead.Domain.Scheduling.EnrollmentRules;

namespace ZenLead.Application.UseCases.Sending;

public record UnsubscribeResult(bool Valid, string? MaskedEmail, bool AlreadyUnsubscribed)
{
    public static readonly UnsubscribeResult Invalid = new(false, null, false);
}

/// <summary>
/// The public unsubscribe flow (F23.4). The signed token is the only credential; any bad token, tampered token or unknown lead
/// gives the same "invalid" answer. <see cref="GetAsync"/> never changes anything (mail scanners prefetch links).
/// </summary>
public class UnsubscribeUseCase(
    IUnsubscribeTokens tokens, ILeadRepository leads, ICampaignEnrollmentRepository enrollments, ISuppressionList suppression)
{
    public async Task<UnsubscribeResult> GetAsync(string token, CancellationToken ct = default)
    {
        if (!tokens.TryParse(token, out var workspaceId, out var leadId)) return UnsubscribeResult.Invalid;
        var lead = await leads.GetForJobAsync(workspaceId, leadId, ct);
        if (lead is null) return UnsubscribeResult.Invalid;
        var already = lead.Status == LeadStatus.Unsubscribed || await suppression.ContainsAsync(workspaceId, lead.Email, ct);
        return new UnsubscribeResult(true, Mask(lead.Email), already);
    }

    public async Task<UnsubscribeResult> ExecuteAsync(string token, CancellationToken ct = default)
    {
        if (!tokens.TryParse(token, out var workspaceId, out var leadId)) return UnsubscribeResult.Invalid;
        var lead = await leads.GetForJobAsync(workspaceId, leadId, ct);                                          // includes soft-deleted
        if (lead is null) return UnsubscribeResult.Invalid;

        await suppression.AddAsync(workspaceId, lead.Email, SuppressionReason.Unsubscribed, lead.Id, ct);       // idempotent upsert
        if (LeadStatusRules.CanTransition(lead.Status, LeadStatus.Unsubscribed)) lead.Status = LeadStatus.Unsubscribed;
        await enrollments.StopAllForLeadAsync(workspaceId, lead.Id, StopEvent.Unsubscribed, ct);                // every campaign
        await leads.UpdateAsync(lead, ct);                                                                       // one save: lead + enrollments
        return new UnsubscribeResult(true, Mask(lead.Email), true);
    }

    /// <summary><c>jane.doe@acme.com</c> → <c>j***@acme.com</c>: the landing page is public.</summary>
    public static string Mask(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0) return "***";
        return $"{email[0]}***{email[at..]}";
    }
}
