using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class LeadDiscoveryRun : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? TargetProfileId { get; set; }            // null if the profile was deleted later (FK SetNull)
    public string CriteriaJson { get; set; } = "{}";      // snapshot at run time
    public string Provider { get; set; } = string.Empty;
    public int RequestedCount { get; set; }               // wanted NEW leads (imported), capped
    public int FoundCount { get; set; }                   // provider results seen (with or without email)
    public int ImportedCount { get; set; }
    public int SkippedDuplicateCount { get; set; }
    public int SkippedSuppressedCount { get; set; }
    public int NoEmailCount { get; set; }
    public int CreditsUsed { get; set; }
    public int PagesFetched { get; set; }
    public string? Cursor { get; set; }                   // resume point
    public DiscoveryRunStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
