using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Campaigns;

/// <summary>Window times are "HH:mm" local time in the campaign's zone (the workspace's unless <c>TimeZoneId</c> overrides it).</summary>
public record CampaignRequest(string Name, string? FromName, int DailySendCap, string WindowStart, string WindowEnd, SendDays SendDays, string? TimeZoneId);
public record StepRequest(int DelayDays, string SubjectTemplate, string BodyTemplate, bool UseAiPersonalisation);
public record ReorderStepsRequest(IReadOnlyList<Guid> OrderedStepIds);
public record EnrollRequest(IReadOnlyList<Guid>? LeadIds, LeadQuery? Filter, bool DryRun = false);
public record PreviewRequest(Guid LeadId, Guid StepId);

public record StepResponse(Guid Id, int Order, int DelayDays, string SubjectTemplate, string BodyTemplate, bool UseAiPersonalisation);
public record CampaignResponse(Guid Id, string Name, CampaignStatus Status, string? FromName, int DailySendCap, string WindowStart, string WindowEnd,
                               SendDays SendDays, string? TimeZoneId, string EffectiveTimeZone, DateTime CreatedAt, DateTime? ActivatedAt,
                               IReadOnlyList<StepResponse> Steps, EnrollmentCounts Counts);

/// <summary><c>Active</c> counts live enrollments (Active or Paused).</summary>
public record EnrollmentCounts(int Total, int Active, int Completed, int Replied, int Unsubscribed, int Bounced, int Failed)
{
    public static readonly EnrollmentCounts Empty = new(0, 0, 0, 0, 0, 0, 0);
}

public record CampaignListItem(Guid Id, string Name, CampaignStatus Status, int StepCount, EnrollmentCounts Counts, DateTime CreatedAt);

public record EnrollResult(int Enrolled, int AlreadyEnrolled, int SkippedSuppressed, int SkippedInvalidEmail, int SkippedMissingTokens,
                           int NotFound, int WarnUnverified, int WarnRisky, bool Truncated);

/// <summary>Codes: no_steps, steps_unordered, first_delay, empty_subject, empty_body, bad_token, window_invalid, domain_unverified.</summary>
public record ActivationProblem(string Code, string Message);

public record EnrollmentRow(Guid Id, Guid LeadId, string LeadName, string LeadEmail, int NextStepOrder, DateTime? NextSendAt, EnrollmentStatus Status, string? FailureReason);

public record PreviewResponse(string Subject, string Body, IReadOnlyList<string> MissingTokens);
