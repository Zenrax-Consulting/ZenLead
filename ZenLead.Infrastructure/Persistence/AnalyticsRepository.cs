using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// EF analytics queries (F28). Every query is explicitly scoped with <see cref="TenantQueryExtensions.ForWorkspace{T}"/> (the caller's
/// JWT workspace) and aggregates in SQL; what comes back is groups, 15-minute buckets and replied enrollments, never message rows.
/// </summary>
public class AnalyticsRepository(ZenLeadDbContext db) : IAnalyticsRepository
{
    private const int InChunk = 500;

    public Task<string?> GetWorkspaceTimeZoneAsync(Guid workspaceId, CancellationToken ct)
        => db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => (string?)w.TimeZone).FirstOrDefaultAsync(ct);

    public Task<CampaignInfo?> GetCampaignAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
        => db.Campaigns.ForWorkspace(workspaceId).AsNoTracking().Where(c => c.Id == campaignId)
            .Select(c => new CampaignInfo(c.Id, c.Name, c.Status)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CampaignInfo>> GetCampaignsAsync(Guid workspaceId, IReadOnlyCollection<Guid> campaignIds, CancellationToken ct)
    {
        var result = new List<CampaignInfo>();
        foreach (var chunk in campaignIds.Chunk(InChunk))
            result.AddRange(await db.Campaigns.ForWorkspace(workspaceId).AsNoTracking().Where(c => chunk.Contains(c.Id))
                .Select(c => new CampaignInfo(c.Id, c.Name, c.Status)).ToListAsync(ct));
        return result;
    }

    public async Task<IReadOnlyList<StepInfo>> GetStepsAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
        => await db.CampaignSteps.ForWorkspace(workspaceId).AsNoTracking().Where(s => s.CampaignId == campaignId).OrderBy(s => s.Order)
            .Select(s => new StepInfo(s.Id, s.Order, s.SubjectTemplate)).ToListAsync(ct);

    private IQueryable<EmailMessage> Cohort(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc)
    {
        var q = db.EmailMessages.ForWorkspace(workspaceId).AsNoTracking().Where(m => m.SentAt != null && m.SentAt >= fromUtc && m.SentAt < toUtc);
        return campaignId is { } id ? q.Where(m => m.CampaignId == id) : q;
    }

    public async Task<IReadOnlyDictionary<Guid, CohortCounts>> CountsByCampaignAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await Cohort(workspaceId, campaignId, fromUtc, toUtc).GroupBy(m => m.CampaignId).Select(g => new
        {
            g.Key,
            Sent = g.Count(),
            Delivered = g.Count(m => m.DeliveredAt != null),
            Opened = g.Count(m => m.OpenedAt != null),
            Bounced = g.Count(m => m.BouncedAt != null || m.Status == EmailMessageStatus.Bounced || m.Status == EmailMessageStatus.Dropped),
            Contacted = g.Select(m => m.EnrollmentId).Distinct().Count()
        }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => new CohortCounts(r.Sent, r.Delivered, r.Opened, r.Bounced, r.Contacted));
    }

    public async Task<IReadOnlyDictionary<Guid, CohortCounts>> CountsByStepAsync(Guid workspaceId, Guid campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await Cohort(workspaceId, campaignId, fromUtc, toUtc).GroupBy(m => m.StepId).Select(g => new
        {
            g.Key,
            Sent = g.Count(),
            Delivered = g.Count(m => m.DeliveredAt != null),
            Opened = g.Count(m => m.OpenedAt != null),
            Bounced = g.Count(m => m.BouncedAt != null || m.Status == EmailMessageStatus.Bounced || m.Status == EmailMessageStatus.Dropped),
            Contacted = g.Select(m => m.EnrollmentId).Distinct().Count()
        }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => new CohortCounts(r.Sent, r.Delivered, r.Opened, r.Bounced, r.Contacted));
    }

    public async Task<IReadOnlyList<ReplyFact>> RepliesAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var cohortEnrollments = Cohort(workspaceId, campaignId, fromUtc, toUtc).Select(m => m.EnrollmentId);
        var rows = await (from i in db.InboxMessages.ForWorkspace(workspaceId)
                          join t in db.InboxThreads.ForWorkspace(workspaceId) on i.ThreadId equals t.Id
                          join e in db.CampaignEnrollments.ForWorkspace(workspaceId) on t.EnrollmentId equals (Guid?)e.Id
                          where i.Direction == MessageDirection.Inbound && !i.IsAutoReply && cohortEnrollments.Contains(e.Id)
                          group i.OccurredAt by new { e.Id, e.CampaignId } into g
                          select new { g.Key.Id, g.Key.CampaignId, First = g.Min() })
            .AsNoTracking().ToListAsync(ct);
        return rows.Select(r => new ReplyFact(r.Id, r.CampaignId, r.First)).ToList();
    }

    public async Task<IReadOnlyList<CohortSend>> CohortSendsAsync(Guid workspaceId, Guid campaignId, DateTime fromUtc, DateTime toUtc,
        IReadOnlyCollection<Guid> enrollmentIds, CancellationToken ct)
    {
        var result = new List<CohortSend>();
        foreach (var chunk in enrollmentIds.Distinct().Chunk(InChunk))
            result.AddRange(await Cohort(workspaceId, campaignId, fromUtc, toUtc).Where(m => chunk.Contains(m.EnrollmentId))
                .Select(m => new CohortSend(m.EnrollmentId, m.StepId, m.SentAt!.Value)).ToListAsync(ct));
        return result;
    }

    public async Task<IReadOnlyList<TimeBucketCount>> SentBucketsAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await Cohort(workspaceId, campaignId, fromUtc, toUtc)
            .Select(m => m.SentAt!.Value)
            .GroupBy(t => new { t.Date, t.Hour, Quarter = t.Minute / 15 })
            .Select(g => new { g.Key.Date, g.Key.Hour, g.Key.Quarter, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => new TimeBucketCount(Bucket(r.Date, r.Hour, r.Quarter), r.Count)).ToList();
    }

    public async Task<IReadOnlyList<TimeBucketCount>> OpenedBucketsAsync(Guid workspaceId, Guid? campaignId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await Cohort(workspaceId, campaignId, fromUtc, toUtc)
            .Where(m => m.OpenedAt != null && m.OpenedAt >= fromUtc && m.OpenedAt < toUtc)
            .Select(m => m.OpenedAt!.Value)
            .GroupBy(t => new { t.Date, t.Hour, Quarter = t.Minute / 15 })
            .Select(g => new { g.Key.Date, g.Key.Hour, g.Key.Quarter, Count = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => new TimeBucketCount(Bucket(r.Date, r.Hour, r.Quarter), r.Count)).ToList();
    }

    private static DateTime Bucket(DateTime date, int hour, int quarter)
        => DateTime.SpecifyKind(date.Date.AddHours(hour).AddMinutes(quarter * 15), DateTimeKind.Utc);
}
