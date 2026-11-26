using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

public class CampaignRepository(ZenLeadDbContext db) : ICampaignRepository
{
    public async Task AddAsync(Campaign campaign, CancellationToken ct = default)
    {
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
    }

    public Task<Campaign?> GetWithStepsAsync(Guid id, CancellationToken ct = default)
        => db.Campaigns.Include(c => c.Steps.OrderBy(s => s.Order)).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<CampaignListItem>> ListWithCountsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        var counts = db.CampaignEnrollments
            .GroupBy(e => e.CampaignId)
            .Select(g => new
            {
                CampaignId = g.Key,
                Total = g.Count(),
                Active = g.Count(e => e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Paused),
                Completed = g.Count(e => e.Status == EnrollmentStatus.Completed),
                Replied = g.Count(e => e.Status == EnrollmentStatus.Replied),
                Unsubscribed = g.Count(e => e.Status == EnrollmentStatus.Unsubscribed),
                Bounced = g.Count(e => e.Status == EnrollmentStatus.Bounced),
                Failed = g.Count(e => e.Status == EnrollmentStatus.Failed)
            });

        var rows = await (
            from c in db.Campaigns.AsNoTracking().Where(c => c.WorkspaceId == workspaceId)
            join k in counts on c.Id equals k.CampaignId into kj
            from k in kj.DefaultIfEmpty()
            orderby c.CreatedAt descending, c.Id
            select new
            {
                c.Id, c.Name, c.Status, c.CreatedAt,
                StepCount = c.Steps.Count,
                Total = (int?)k.Total ?? 0,
                Active = (int?)k.Active ?? 0,
                Completed = (int?)k.Completed ?? 0,
                Replied = (int?)k.Replied ?? 0,
                Unsubscribed = (int?)k.Unsubscribed ?? 0,
                Bounced = (int?)k.Bounced ?? 0,
                Failed = (int?)k.Failed ?? 0
            }).ToListAsync(ct);

        return rows.Select(r => new CampaignListItem(r.Id, r.Name, r.Status, r.StepCount,
            new EnrollmentCounts(r.Total, r.Active, r.Completed, r.Replied, r.Unsubscribed, r.Bounced, r.Failed), r.CreatedAt)).ToList();
    }

    public async Task UpdateAsync(Campaign campaign, CancellationToken ct = default)
    {
        campaign.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);                     // campaign is tracked (loaded via GetWithStepsAsync in the same scope)
    }

    public Task AddStepAsync(CampaignStep step, CancellationToken ct = default)
    {
        db.CampaignSteps.Add(step);
        return Task.CompletedTask;
    }

    public Task RemoveStepAsync(CampaignStep step, CancellationToken ct = default)
    {
        db.CampaignSteps.Remove(step);
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
