using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// EF persistence for the campaign sender. Runs in a Hangfire job (no current workspace), so every query is explicitly
/// scoped with <see cref="TenantQueryExtensions.ForWorkspace{T}"/>, except <see cref="GetDueAsync"/>: the one deliberate
/// cross-workspace read, from which only ids leave.
/// </summary>
public class SenderStore(ZenLeadDbContext db) : ISenderStore
{
    public async Task<IReadOnlyList<DueEnrollment>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct)
    {
        if (limit <= 0) return [];
        var due = from e in db.CampaignEnrollments.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter])
                  join c in db.Campaigns.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]) on e.CampaignId equals c.Id
                  where e.Status == EnrollmentStatus.Active && e.NextSendAt != null && e.NextSendAt <= nowUtc
                        && c.Status == CampaignStatus.Active && c.WorkspaceId == e.WorkspaceId
                  select e;

        var workspaces = await due.Select(e => e.WorkspaceId).Distinct().ToListAsync(ct);
        var perWorkspace = new List<List<DueEnrollment>>();
        foreach (var workspaceId in workspaces)
            perWorkspace.Add(await due.Where(e => e.WorkspaceId == workspaceId)
                .OrderBy(e => e.NextSendAt).ThenBy(e => e.Id).Take(limit)
                .Select(e => new DueEnrollment(e.Id, e.WorkspaceId, e.CampaignId, e.NextSendAt!.Value))
                .ToListAsync(ct));

        // round-robin across workspaces (oldest-first within each), so a workspace with thousands due can't fill the batch
        var result = new List<DueEnrollment>(limit);
        for (var i = 0; result.Count < limit; i++)
        {
            var any = false;
            foreach (var list in perWorkspace.Where(l => i < l.Count))
            {
                any = true;
                result.Add(list[i]);
                if (result.Count == limit) break;
            }
            if (!any) break;
        }
        return result;
    }

    public async Task<SendContext?> LoadAsync(Guid workspaceId, Guid enrollmentId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();                                            // each enrollment is its own unit of work
        var enrollment = await db.CampaignEnrollments.ForWorkspace(workspaceId).FirstOrDefaultAsync(e => e.Id == enrollmentId, ct);
        if (enrollment is null) return null;
        var campaign = await db.Campaigns.ForWorkspace(workspaceId).Include(c => c.Steps)
            .FirstOrDefaultAsync(c => c.Id == enrollment.CampaignId, ct);
        var lead = await db.Leads.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter, ZenLeadDbContext.SoftDeleteFilter])
            .Where(l => l.WorkspaceId == workspaceId).Include(l => l.Company)
            .FirstOrDefaultAsync(l => l.Id == enrollment.LeadId, ct);
        if (campaign is null || lead is null) return null;
        var timeZone = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
        return new SendContext { Enrollment = enrollment, Campaign = campaign, Lead = lead, WorkspaceTimeZone = timeZone };
    }

    public Task<int> CountMessagesSinceAsync(Guid workspaceId, Guid? campaignId, DateTime sinceUtc, CancellationToken ct)
    {
        var query = db.EmailMessages.ForWorkspace(workspaceId)
            .Where(m => m.QueuedAt >= sinceUtc && m.Status != EmailMessageStatus.Failed && m.Status != EmailMessageStatus.Dropped);
        if (campaignId is { } id) query = query.Where(m => m.CampaignId == id);
        return query.CountAsync(ct);
    }

    public Task<EmailMessage?> FindMessageAsync(Guid enrollmentId, Guid stepId, CancellationToken ct)
        => db.EmailMessages.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter])
            .FirstOrDefaultAsync(m => m.EnrollmentId == enrollmentId && m.StepId == stepId, ct);   // ids came from a ForWorkspace load

    public async Task<bool> TryInsertQueuedAsync(EmailMessage message, CancellationToken ct)
    {
        db.EmailMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(message).State = EntityState.Detached;
            // provider-agnostic: if a row for (enrollment, step) now exists, the unique index is what failed
            var exists = await db.EmailMessages.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]).AsNoTracking()
                .AnyAsync(m => m.EnrollmentId == message.EnrollmentId && m.StepId == message.StepId, ct);
            if (exists) return false;
            throw;
        }
    }

    public async Task DeleteMessageAsync(Guid messageId, CancellationToken ct)
    {
        var tracked = db.EmailMessages.Local.FirstOrDefault(m => m.Id == messageId)
                      ?? await db.EmailMessages.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]).FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (tracked is not null) db.EmailMessages.Remove(tracked);           // staged; persisted by the next SaveAsync
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();                                        // never let one enrollment's half-applied changes leak into the next save
            throw;
        }
    }

    public Task<Campaign?> GetCampaignAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
        => db.Campaigns.ForWorkspace(workspaceId).FirstOrDefaultAsync(c => c.Id == campaignId, ct);

    public Task<bool> CampaignHasLiveEnrollmentsAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
        => db.CampaignEnrollments.ForWorkspace(workspaceId)
            .AnyAsync(e => e.CampaignId == campaignId && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Paused), ct);
}
