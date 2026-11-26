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
/// <remarks>
/// <c>StepsStarted</c> (F24) = how many steps any enrollment has already passed (<c>MaxNextStepOrder − 1</c>, never negative): the builder
/// pins those steps (no delete/reorder) before the server would answer 409. <c>WorkspaceTimeZone</c> lets the UI show the zone an override replaces.
/// </remarks>
public record CampaignResponse(Guid Id, string Name, CampaignStatus Status, string? FromName, int DailySendCap, string WindowStart, string WindowEnd,
                               SendDays SendDays, string? TimeZoneId, string EffectiveTimeZone, DateTime CreatedAt, DateTime? ActivatedAt,
                               IReadOnlyList<StepResponse> Steps, EnrollmentCounts Counts, int StepsStarted, string WorkspaceTimeZone);

/// <summary><c>Active</c> counts live enrollments (Active or Paused).</summary>
public record EnrollmentCounts(int Total, int Active, int Completed, int Replied, int Unsubscribed, int Bounced, int Failed)
{
    public static readonly EnrollmentCounts Empty = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary><c>Sent</c> (F24) = campaign emails the provider accepted (status Sent, Delivered or Bounced).</summary>
public record CampaignListItem(Guid Id, string Name, CampaignStatus Status, int StepCount, EnrollmentCounts Counts, DateTime CreatedAt, int Sent);

public record EnrollResult(int Enrolled, int AlreadyEnrolled, int SkippedSuppressed, int SkippedInvalidEmail, int SkippedMissingTokens,
                           int NotFound, int WarnUnverified, int WarnRisky, bool Truncated);

/// <summary>Codes: no_steps, steps_unordered, first_delay, empty_subject, empty_body, bad_token, window_invalid, domain_unverified.
/// <c>StepOrder</c> (F24) names the step a step-level problem is about, so the UI can link straight to it.</summary>
public record ActivationProblem(string Code, string Message, int? StepOrder = null);

public record EnrollmentRow(Guid Id, Guid LeadId, string LeadName, string LeadEmail, int NextStepOrder, DateTime? NextSendAt, EnrollmentStatus Status, string? FailureReason);

public record PreviewResponse(string Subject, string Body, IReadOnlyList<string> MissingTokens);

/// <summary>The campaign sending identity (F24), from <c>EmailOptions</c>. No secrets.</summary>
public record SenderStatus(string FromAddress, string FromName, bool DomainVerified, string InboundDomain);

/// <summary>
/// What activating would cost in AI (F24). <c>EstimatedAiCalls</c> = <c>EnrolledActive × AiSteps</c>: an upper bound, since leads that reply,
/// bounce or are already past a step never get those calls.
/// </summary>
public record ActivationPreview(int EnrolledActive, int AiSteps, int EstimatedAiCalls, decimal EstimatedAiCostUsd, decimal AiBudgetRemainingUsd);
