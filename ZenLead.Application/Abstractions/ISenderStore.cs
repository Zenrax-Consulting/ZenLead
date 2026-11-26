using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

public record DueEnrollment(Guid EnrollmentId, Guid WorkspaceId, Guid CampaignId, DateTime NextSendAt);

public class SendContext
{
    public required CampaignEnrollment Enrollment { get; init; }       // tracked
    public required Campaign Campaign { get; init; }                   // with Steps
    public required Lead Lead { get; init; }                           // tracked, with Company; soft-deleted leads included
    public required string WorkspaceTimeZone { get; init; }
}

/// <summary>
/// Every persistence call the campaign sender needs. The EF implementation scopes everything with <c>ForWorkspace</c> except
/// <see cref="GetDueAsync"/>, the one deliberate cross-workspace read (only ids leave it).
/// </summary>
public interface ISenderStore
{
    /// <summary>
    /// Active campaign + Active enrollment + NextSendAt &lt;= now, oldest first within a workspace and interleaved across workspaces,
    /// so one busy workspace can't fill the batch and starve another.
    /// </summary>
    Task<IReadOnlyList<DueEnrollment>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct);

    /// <summary>Starts a fresh unit of work (earlier enrollments' tracked state is dropped) and loads one enrollment to send.</summary>
    Task<SendContext?> LoadAsync(Guid workspaceId, Guid enrollmentId, CancellationToken ct);

    /// <summary>Queued + Sent + Delivered + Bounced (not Failed/Dropped), by QueuedAt. <paramref name="campaignId"/> null = whole workspace.</summary>
    Task<int> CountMessagesSinceAsync(Guid workspaceId, Guid? campaignId, DateTime sinceUtc, CancellationToken ct);

    Task<EmailMessage?> FindMessageAsync(Guid enrollmentId, Guid stepId, CancellationToken ct);

    /// <summary>Inserts and saves at once. False (never an exception) when the unique (EnrollmentId, StepId) index rejects it.</summary>
    Task<bool> TryInsertQueuedAsync(EmailMessage message, CancellationToken ct);

    Task DeleteMessageAsync(Guid messageId, CancellationToken ct);

    /// <summary>Persists tracked changes (enrollment, lead, message) in one save. On failure the unsaved changes are discarded.</summary>
    Task SaveAsync(CancellationToken ct);

    /// <summary>Tracked, without steps.</summary>
    Task<Campaign?> GetCampaignAsync(Guid workspaceId, Guid campaignId, CancellationToken ct);

    Task<bool> CampaignHasLiveEnrollmentsAsync(Guid workspaceId, Guid campaignId, CancellationToken ct);
}

/// <summary>Addresses a workspace must never email. Checked at ingestion, enrollment and send. Emails are compared lowercase.</summary>
public interface ISuppressionList
{
    Task<bool> ContainsAsync(Guid workspaceId, string email, CancellationToken ct);

    /// <summary>For ingestion/enrollment batches. Returns the suppressed subset, lowercase.</summary>
    Task<IReadOnlySet<string>> FilterSuppressedAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct);

    /// <summary>Idempotent upsert; saves immediately.</summary>
    Task AddAsync(Guid workspaceId, string email, SuppressionReason reason, Guid? leadId, CancellationToken ct);
}
