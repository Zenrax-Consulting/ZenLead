using ZenLead.Application.Abstractions;
using ZenLead.Application.Analytics;
using ZenLead.Application.Dtos.Analytics;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.UseCases.Analytics;

public enum AnalyticsOutcomeKind { Ok, InvalidRange, NotFound }

public record AnalyticsOutcome<T>(AnalyticsOutcomeKind Kind, T? Value = null, string? Error = null) where T : class;

/// <summary>Workspace and per-campaign analytics (F28). Ranges are local days in the workspace time zone (<c>Workspace.TimeZone</c>).</summary>
public class AnalyticsUseCase(IAnalyticsRepository repo, TimeProvider clock)
{
    public async Task<AnalyticsOutcome<WorkspaceAnalytics>> WorkspaceAsync(Guid workspaceId, AnalyticsQuery query, CancellationToken ct)
    {
        var (zoneId, zone) = await ZoneAsync(workspaceId, ct);
        if (!DateRange.TryResolve(query, zone, clock.GetUtcNow().UtcDateTime, out var range, out var error))
            return new(AnalyticsOutcomeKind.InvalidRange, Error: error);

        var byCampaign = await repo.CountsByCampaignAsync(workspaceId, null, range.FromUtc, range.ToUtcExclusive, ct);
        var replies = await repo.RepliesAsync(workspaceId, null, range.FromUtc, range.ToUtcExclusive, ct);
        var trend = await TrendAsync(workspaceId, null, range, zone, replies, ct);
        var campaigns = await repo.GetCampaignsAsync(workspaceId, byCampaign.Keys.ToList(), ct);

        var repliedByCampaign = replies.GroupBy(r => r.CampaignId).ToDictionary(g => g.Key, g => g.Count());
        var rows = campaigns
            .Select(c => new CampaignRow(c.Id, c.Name, c.Status, Totals(byCampaign[c.Id], repliedByCampaign.GetValueOrDefault(c.Id))))
            .OrderByDescending(r => r.Totals.Sent).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var all = byCampaign.Values.Aggregate(CohortCounts.Zero, (a, b) => a.Add(b));   // enrollments are per campaign, so distinct counts add up
        return new(AnalyticsOutcomeKind.Ok, new WorkspaceAnalytics(range.From, range.To, zoneId, Totals(all, replies.Count), trend, rows));
    }

    public async Task<AnalyticsOutcome<CampaignAnalytics>> CampaignAsync(Guid workspaceId, Guid campaignId, AnalyticsQuery query, CancellationToken ct)
    {
        var campaign = await repo.GetCampaignAsync(workspaceId, campaignId, ct);
        if (campaign is null) return new(AnalyticsOutcomeKind.NotFound);              // another workspace's campaign is indistinguishable from none
        var (zoneId, zone) = await ZoneAsync(workspaceId, ct);
        if (!DateRange.TryResolve(query, zone, clock.GetUtcNow().UtcDateTime, out var range, out var error))
            return new(AnalyticsOutcomeKind.InvalidRange, Error: error);

        var counts = (await repo.CountsByCampaignAsync(workspaceId, campaignId, range.FromUtc, range.ToUtcExclusive, ct)).GetValueOrDefault(campaignId) ?? CohortCounts.Zero;
        var replies = await repo.RepliesAsync(workspaceId, campaignId, range.FromUtc, range.ToUtcExclusive, ct);
        var trend = await TrendAsync(workspaceId, campaignId, range, zone, replies, ct);
        var steps = await StepsAsync(workspaceId, campaignId, range, replies, ct);
        return new(AnalyticsOutcomeKind.Ok, new CampaignAnalytics(campaign.Id, campaign.Name, campaign.Status, range.From, range.To, zoneId,
            Totals(counts, replies.Count), steps, trend));
    }

    public static AnalyticsTotals Totals(CohortCounts c, int repliedLeads)
        => new(c.Sent, c.Delivered, c.Opened, c.Bounced, c.Contacted, repliedLeads,
            DeliveredRate: RateMath.Rate(c.Delivered, c.Sent),
            OpenRate: RateMath.Rate(c.Opened, c.Delivered),
            ReplyRate: RateMath.Rate(repliedLeads, c.Contacted),
            BounceRate: RateMath.Rate(c.Bounced, c.Sent));

    private async Task<(string Id, TimeZoneInfo Zone)> ZoneAsync(Guid workspaceId, CancellationToken ct)
    {
        var id = await repo.GetWorkspaceTimeZoneAsync(workspaceId, ct);
        return TimeZones.TryFind(id, out var zone) ? (id!, zone) : ("UTC", TimeZoneInfo.Utc);
    }

    private async Task<IReadOnlyList<TrendPoint>> TrendAsync(Guid workspaceId, Guid? campaignId, ResolvedRange range, TimeZoneInfo zone,
        IReadOnlyList<ReplyFact> replies, CancellationToken ct)
    {
        var sent = await repo.SentBucketsAsync(workspaceId, campaignId, range.FromUtc, range.ToUtcExclusive, ct);
        var opened = await repo.OpenedBucketsAsync(workspaceId, campaignId, range.FromUtc, range.ToUtcExclusive, ct);
        return BuildTrend(range, zone, sent, opened, replies);
    }

    /// <summary>One point per local day of the range (zero-filled; DST days included exactly once).</summary>
    public static IReadOnlyList<TrendPoint> BuildTrend(ResolvedRange range, TimeZoneInfo zone, IReadOnlyList<TimeBucketCount> sent,
        IReadOnlyList<TimeBucketCount> opened, IReadOnlyList<ReplyFact> replies)
    {
        var days = new (int Sent, int Opened, int Replies)[range.Days];
        int? Index(DateTime utc)
        {
            var i = DateRange.LocalDay(utc, zone).DayNumber - range.From.DayNumber;
            return i >= 0 && i < days.Length ? i : null;
        }
        foreach (var b in sent) if (Index(b.BucketStartUtc) is { } i) days[i].Sent += b.Count;
        foreach (var b in opened) if (Index(b.BucketStartUtc) is { } i) days[i].Opened += b.Count;
        foreach (var r in replies)
            if (r.FirstReplyAt >= range.FromUtc && r.FirstReplyAt < range.ToUtcExclusive && Index(r.FirstReplyAt) is { } i) days[i].Replies++;
        return days.Select((d, i) => new TrendPoint(range.From.AddDays(i), d.Sent, d.Opened, d.Replies)).ToList();
    }

    private async Task<IReadOnlyList<StepBreakdown>> StepsAsync(Guid workspaceId, Guid campaignId, ResolvedRange range, IReadOnlyList<ReplyFact> replies, CancellationToken ct)
    {
        var steps = await repo.GetStepsAsync(workspaceId, campaignId, ct);
        var counts = await repo.CountsByStepAsync(workspaceId, campaignId, range.FromUtc, range.ToUtcExclusive, ct);
        var sends = replies.Count == 0 ? [] : await repo.CohortSendsAsync(workspaceId, campaignId, range.FromUtc, range.ToUtcExclusive,
            replies.Select(r => r.EnrollmentId).ToList(), ct);
        var repliesByStep = AttributeReplies(replies, sends);

        var rows = steps.Select(s =>
        {
            var c = counts.GetValueOrDefault(s.Id) ?? CohortCounts.Zero;
            return new StepBreakdown(s.Id, s.Order, s.SubjectTemplate, c.Sent, c.Delivered, c.Opened, c.Bounced, repliesByStep.GetValueOrDefault(s.Id));
        }).ToList();
        // mail sent from a step that was later removed from the sequence still counts
        foreach (var (stepId, c) in counts.Where(kv => steps.All(s => s.Id != kv.Key)))
            rows.Add(new StepBreakdown(stepId, 0, "(removed step)", c.Sent, c.Delivered, c.Opened, c.Bounced, repliesByStep.GetValueOrDefault(stepId)));
        return rows.OrderBy(r => r.Order == 0 ? int.MaxValue : r.Order).ToList();
    }

    /// <summary>Each replied lead counts once, for the latest step sent to it at or before its first reply.</summary>
    public static Dictionary<Guid, int> AttributeReplies(IReadOnlyList<ReplyFact> replies, IReadOnlyList<CohortSend> sends)
    {
        var byEnrollment = sends.GroupBy(s => s.EnrollmentId).ToDictionary(g => g.Key, g => g.OrderBy(s => s.SentAt).ToList());
        var result = new Dictionary<Guid, int>();
        foreach (var r in replies)
        {
            if (!byEnrollment.TryGetValue(r.EnrollmentId, out var list)) continue;
            var step = list.LastOrDefault(s => s.SentAt <= r.FirstReplyAt);
            if (step is null) continue;
            result[step.StepId] = result.GetValueOrDefault(step.StepId) + 1;
        }
        return result;
    }
}
