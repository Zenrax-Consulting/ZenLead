using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

/// <summary>Aggregates over the cohort of messages sent in [fromUtc, toUtc). <c>Contacted</c> = distinct enrollments.</summary>
public record CohortCounts(int Sent, int Delivered, int Opened, int Bounced, int Contacted)
{
    public static readonly CohortCounts Zero = new(0, 0, 0, 0, 0);
    public CohortCounts Add(CohortCounts o) => new(Sent + o.Sent, Delivered + o.Delivered, Opened + o.Opened, Bounced + o.Bounced, Contacted + o.Contacted);
}

/// <summary>A cohort enrollment's FIRST human (non-auto) reply.</summary>
public record ReplyFact(Guid EnrollmentId, Guid CampaignId, DateTime FirstReplyAt);

/// <summary>Messages of the cohort counted per 15-minute UTC bucket. Every zone offset is a multiple of 15 minutes, so a bucket never straddles a local midnight.</summary>
public record TimeBucketCount(DateTime BucketStartUtc, int Count);

public record CampaignInfo(Guid Id, string Name, CampaignStatus Status);
public record StepInfo(Guid Id, int Order, string SubjectTemplate);
public record CohortSend(Guid EnrollmentId, Guid StepId, DateTime SentAt);

/// <summary>
/// Read-only analytics queries (F28). Every query is scoped to <c>workspaceId</c>; aggregates run in SQL over the
/// (WorkspaceId, CampaignId[, StepId], SentAt) indexes and only small results (groups, buckets, replied enrollments) come back.
/// </summary>
public interface IAnalyticsRepository
{
    Task<string?> GetWorkspaceTimeZoneAsync(Guid workspaceId, CancellationToken ct);
    Task<CampaignInfo?> GetCampaignAsync(Guid workspaceId, Guid campaignId, CancellationToken ct);
    Task<IReadOnlyList<CampaignInfo>> GetCampaignsAsync(Guid workspaceId, IReadOnlyCollection<Guid> campaignIds, CancellationToken ct);
    Task<IReadOnlyList<StepInfo>> GetStepsAsync(Guid workspaceId, Guid campaignId, CancellationToken ct);

    /// <summary>Cohort counts per campaign (only campaigns with at least one message in the cohort).</summary>
    Task<IReadOnlyDictionary<Guid, CohortCounts>> CountsByCampaignAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, CohortCounts>> CountsByStepAsync(Guid workspaceId, Guid campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>Cohort enrollments whose thread has a human inbound message, with the time of the first one.</summary>
    Task<IReadOnlyList<ReplyFact>> RepliesAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>The cohort's sends for these enrollments (per-step reply attribution).</summary>
    Task<IReadOnlyList<CohortSend>> CohortSendsAsync(Guid workspaceId, Guid campaignId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<Guid> enrollmentIds, CancellationToken ct);

    Task<IReadOnlyList<TimeBucketCount>> SentBucketsAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>First opens of cohort messages that happened inside [fromUtc, toUtc).</summary>
    Task<IReadOnlyList<TimeBucketCount>> OpenedBucketsAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}
