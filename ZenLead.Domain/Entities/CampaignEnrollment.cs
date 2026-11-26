using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Entities;

public class CampaignEnrollment : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid LeadId { get; set; }
    public int NextStepOrder { get; set; } = 1;              // 1-based order of the next step to send; steps+1 once completed
    public DateTime? NextSendAt { get; set; }                // UTC; null in Draft campaigns and once finished
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Active;
    public DateTime EnrolledAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? FailureReason { get; set; }
    public int SendAttempts { get; set; }                     // consecutive retryable provider failures for the current step (F23)
    public Lead? Lead { get; set; }
}
