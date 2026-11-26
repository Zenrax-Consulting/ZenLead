using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

/// <summary>The enrollment an inbound email belongs to. Everything the use case writes is scoped to <see cref="WorkspaceId"/>.</summary>
public record EnrollmentRef(Guid EnrollmentId, Guid WorkspaceId, Guid CampaignId, Guid LeadId, string LeadEmail);

/// <param name="LeadMarkedReplied">The lead's status changed to Replied.</param>
/// <param name="EnrollmentStopped">The enrollment went from Active/Paused to Replied (its sequence stopped).</param>
public record InboundApplyResult(bool LeadMarkedReplied, bool EnrollmentStopped);

/// <summary>
/// Persistence for the inbound webhook (F25). It runs with no current workspace: cross-tenant lookups are explicit and by
/// unguessable id or by our own sent-message ids; everything after the workspace is known is scoped with <c>ForWorkspace</c>.
/// </summary>
public interface IInboundStore
{
    /// <summary>Level 1: the Reply-To token. A primary-key lookup across workspaces.</summary>
    Task<EnrollmentRef?> FindEnrollmentByIdAsync(Guid enrollmentId, CancellationToken ct);

    /// <summary>Level 2: <c>In-Reply-To</c>/<c>References</c> ids against <c>EmailMessage.ProviderMessageId</c>/<c>SmtpMessageId</c>.</summary>
    Task<EnrollmentRef?> FindByMessageReferencesAsync(IReadOnlyCollection<string> messageIds, CancellationToken ct);

    /// <summary>Level 3: leads with this email that we sent to since <paramref name="sentSinceUtc"/>; at most one (the latest) per workspace.</summary>
    Task<IReadOnlyList<EnrollmentRef>> FindRecentByLeadEmailAsync(string email, DateTime sentSinceUtc, CancellationToken ct);

    /// <summary>The enrollment's thread; created (and saved) with <paramref name="subject"/> when none exists yet.</summary>
    Task<InboxThread> GetOrCreateThreadAsync(EnrollmentRef target, string subject, DateTime now, CancellationToken ct);

    /// <summary>
    /// Inserts the inbound message and marks its thread unread (latest message time and classification), in one save.
    /// False, never an exception, when the (WorkspaceId, RawMessageId) dedupe index rejects it.
    /// </summary>
    Task<bool> TryAddInboundAsync(InboxMessage message, CancellationToken ct);

    /// <summary>Human replies: lead → Replied (if allowed) and enrollment stopped. Auto-replies change nothing here. Saves.</summary>
    Task<InboundApplyResult> ApplyReplyEffectsAsync(EnrollmentRef target, bool isAutoReply, CancellationToken ct);

    Task AddQuarantineAsync(InboundQuarantine row, CancellationToken ct);
}
