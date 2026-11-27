using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Analytics;

/// <summary>Dates are local days in the workspace time zone, both inclusive. Default: the last 30 days; at most 366 days.</summary>
public record AnalyticsQuery(DateOnly? From, DateOnly? To);

/// <summary>
/// Cohort figures: every number is about messages SENT in the range and what has happened to them since, so rates can't exceed
/// 100 % and don't shift as the range end moves. Each rate is null when its denominator is 0.
/// <list type="bullet">
/// <item><c>DeliveredRate</c> = Delivered / Sent; <c>BounceRate</c> = Bounced / Sent</item>
/// <item><c>OpenRate</c> = Opened / Delivered (unique messages; approximate — see <see cref="OpensApproximate"/>)</item>
/// <item><c>ReplyRate</c> = RepliedLeads / ContactedLeads (human replies only; auto-replies excluded)</item>
/// </list>
/// </summary>
public record AnalyticsTotals(int Sent, int Delivered, int Opened, int Bounced, int ContactedLeads, int RepliedLeads,
                              double? DeliveredRate, double? OpenRate, double? ReplyRate, double? BounceRate)
{
    /// <summary>Apple Mail Privacy Protection pre-fetches tracking pixels and some clients block them: opens are never exact.</summary>
    public bool OpensApproximate => true;
}

/// <summary>One local day (workspace zone), zero-filled. Sent by send day; Opened by first-open day; Replies = leads whose first human reply fell on that day.</summary>
public record TrendPoint(DateOnly Day, int Sent, int Opened, int Replies);

/// <summary>Replies are attributed to the latest step sent to that lead before the reply.</summary>
public record StepBreakdown(Guid StepId, int Order, string SubjectTemplate, int Sent, int Delivered, int Opened, int Bounced, int Replies);

public record CampaignAnalytics(Guid CampaignId, string Name, CampaignStatus Status, DateOnly From, DateOnly To, string TimeZone,
                                AnalyticsTotals Totals, IReadOnlyList<StepBreakdown> Steps, IReadOnlyList<TrendPoint> Trend);

public record WorkspaceAnalytics(DateOnly From, DateOnly To, string TimeZone, AnalyticsTotals Totals, IReadOnlyList<TrendPoint> Trend,
                                 IReadOnlyList<CampaignRow> Campaigns);

public record CampaignRow(Guid CampaignId, string Name, CampaignStatus Status, AnalyticsTotals Totals);
