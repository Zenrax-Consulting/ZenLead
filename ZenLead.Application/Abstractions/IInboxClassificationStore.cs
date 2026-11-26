using ZenLead.Domain.Entities;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.Abstractions;

/// <summary>Everything <c>ClassificationEffects</c> needs to label a reply and apply its consequences (all tracked).</summary>
/// <param name="Enrollment">The thread's enrollment; null when the thread has none (or it was removed).</param>
/// <param name="Window">The enrollment's campaign send window (UTC 09–17 weekdays when there is no campaign).</param>
/// <param name="IsLatestInbound">The message is the thread's newest inbound message, so its label may drive the thread chip.</param>
public record ClassificationContext(InboxMessage Message, InboxThread Thread, CampaignEnrollment? Enrollment, Lead Lead, SendWindow Window, bool IsLatestInbound);

/// <summary>The message to classify plus the thread's latest outbound (what the lead replied to). Read-only.</summary>
public record ClassificationInput(InboxMessage Message, string? OriginalSubject, string? OriginalBody, Guid? CampaignId, Guid? EnrollmentId);

/// <summary>
/// Persistence for reply classification (F26) and the manual override (F27). Runs in a background job with no current workspace,
/// so every query is scoped with <c>ForWorkspace</c>: a message id from another workspace loads nothing.
/// </summary>
public interface IInboxClassificationStore
{
    Task<ClassificationContext?> LoadAsync(Guid workspaceId, Guid messageId, CancellationToken ct);

    Task<ClassificationInput?> LoadForClassificationAsync(Guid workspaceId, Guid messageId, CancellationToken ct);

    /// <summary>No usable label (budget, invalid output): Unclassified with a note; <c>ClassifiedBy</c> stays None so it can be retried. Saves.</summary>
    Task MarkUnclassifiedAsync(Guid workspaceId, Guid messageId, string note, CancellationToken ct);

    /// <summary>Stops every live enrollment of the lead in this workspace (staged, saved by <see cref="SaveAsync"/>). Returns how many changed.</summary>
    Task<int> StopAllEnrollmentsForLeadAsync(Guid workspaceId, Guid leadId, EnrollmentRules.StopEvent stop, CancellationToken ct);

    /// <summary>Whether the thread has a human (non-auto) inbound message other than <paramref name="messageId"/>.</summary>
    Task<bool> ThreadHasOtherHumanReplyAsync(Guid threadId, Guid messageId, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
