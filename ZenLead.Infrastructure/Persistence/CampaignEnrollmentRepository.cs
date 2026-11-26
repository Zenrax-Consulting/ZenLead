using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Infrastructure.Persistence;

public class CampaignEnrollmentRepository(ZenLeadDbContext db) : ICampaignEnrollmentRepository
{
    private const int StampPageSize = 1000;

    public async Task<IReadOnlySet<Guid>> ExistingLeadIdsAsync(Guid campaignId, IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default)
    {
        if (leadIds.Count == 0) return new HashSet<Guid>();
        var existing = await db.CampaignEnrollments.AsNoTracking()
            .Where(e => e.CampaignId == campaignId && leadIds.Contains(e.LeadId))
            .Select(e => e.LeadId).ToListAsync(ct);
        return existing.ToHashSet();
    }

    public async Task AddRangeAsync(IReadOnlyCollection<CampaignEnrollment> enrollments, CancellationToken ct = default)
    {
        db.CampaignEnrollments.AddRange(enrollments);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var e in enrollments) db.Entry(e).State = EntityState.Detached;
            // provider-agnostic: if one of these leads is now enrolled, the unique (CampaignId, LeadId) index is what failed
            var campaignId = enrollments.First().CampaignId;
            if ((await ExistingLeadIdsAsync(campaignId, enrollments.Select(e => e.LeadId).ToList(), ct)).Count > 0)
                throw new DuplicateEnrollmentException();
            throw;
        }
    }

    public async Task<int> MaxNextStepOrderAsync(Guid campaignId, CancellationToken ct = default)
        => await db.CampaignEnrollments.Where(e => e.CampaignId == campaignId).MaxAsync(e => (int?)e.NextStepOrder, ct) ?? 0;

    public async Task<int> StampNextSendAtAsync(Guid campaignId, SendWindow window, DateTime nowUtc, CancellationToken ct = default)
    {
        var nextSendAt = SendScheduler.NextSendAt(nowUtc, 0, window);
        var stamped = 0;
        while (true)
        {
            // each page leaves the filter once stamped, so always read the first page
            var page = await db.CampaignEnrollments
                .Where(e => e.CampaignId == campaignId && e.Status == EnrollmentStatus.Active && e.NextSendAt == null)
                .OrderBy(e => e.Id).Take(StampPageSize).ToListAsync(ct);
            if (page.Count == 0) return stamped;

            foreach (var e in page) e.NextSendAt = nextSendAt;
            await db.SaveChangesAsync(ct);
            foreach (var e in page) db.Entry(e).State = EntityState.Detached;
            stamped += page.Count;
        }
    }

    public async Task<PagedResult<CampaignEnrollment>> ListAsync(Guid campaignId, EnrollmentStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.CampaignEnrollments.AsNoTracking().Where(e => e.CampaignId == campaignId);
        if (status is { } s) query = query.Where(e => e.Status == s);
        var total = await query.CountAsync(ct);
        var items = await query
            .Include(e => e.Lead)
            .IgnoreQueryFilters([ZenLeadDbContext.SoftDeleteFilter])          // keep rows of soft-deleted leads; the tenant filter stays
            .OrderBy(e => e.EnrolledAt).ThenBy(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<CampaignEnrollment>(items, total, page, pageSize);
    }

    public async Task<int> StopAllForLeadAsync(Guid workspaceId, Guid leadId, EnrollmentRules.StopEvent stop, CancellationToken ct = default)
    {
        var live = await db.CampaignEnrollments.ForWorkspace(workspaceId)
            .Where(e => e.LeadId == leadId && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Paused))
            .ToListAsync(ct);
        return live.Count(e => EnrollmentRules.ApplyStop(e, stop));
    }

    public async Task<EnrollmentCounts> CountsAsync(Guid campaignId, CancellationToken ct = default)
    {
        var byStatus = await db.CampaignEnrollments.AsNoTracking().Where(e => e.CampaignId == campaignId)
            .GroupBy(e => e.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(EnrollmentStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;
        return new EnrollmentCounts(byStatus.Sum(x => x.Count), Of(EnrollmentStatus.Active) + Of(EnrollmentStatus.Paused),
            Of(EnrollmentStatus.Completed), Of(EnrollmentStatus.Replied), Of(EnrollmentStatus.Unsubscribed), Of(EnrollmentStatus.Bounced), Of(EnrollmentStatus.Failed));
    }
}
