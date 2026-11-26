using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// EF persistence for the inbound webhook (F25). The webhook is anonymous, so there is no current workspace: the three matching
/// lookups are deliberate cross-workspace reads (by unguessable enrollment id, by our own sent-message ids, or by sender email
/// with an "exactly one workspace" rule in the use case). Every write after that is scoped with <see cref="TenantQueryExtensions.ForWorkspace{T}"/>
/// to the matched enrollment's workspace.
/// </summary>
public class InboundStore(ZenLeadDbContext db, TimeProvider clock) : IInboundStore
{
    private static readonly string[] AllFilters = [ZenLeadDbContext.TenantFilter, ZenLeadDbContext.SoftDeleteFilter];

    public async Task<EnrollmentRef?> FindEnrollmentByIdAsync(Guid enrollmentId, CancellationToken ct)
    {
        var row = await (from e in db.CampaignEnrollments.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter])
                         join l in db.Leads.IgnoreQueryFilters(AllFilters) on e.LeadId equals l.Id
                         where e.Id == enrollmentId && l.WorkspaceId == e.WorkspaceId
                         select new { e.Id, e.WorkspaceId, e.CampaignId, e.LeadId, l.Email })
            .AsNoTracking().FirstOrDefaultAsync(ct);
        return row is null ? null : new EnrollmentRef(row.Id, row.WorkspaceId, row.CampaignId, row.LeadId, row.Email);
    }

    public async Task<EnrollmentRef?> FindByMessageReferencesAsync(IReadOnlyCollection<string> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0) return null;
        var ids = messageIds.ToList();
        var enrollmentId = await db.EmailMessages.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter])
            .Where(m => (m.ProviderMessageId != null && ids.Contains(m.ProviderMessageId)) || (m.SmtpMessageId != null && ids.Contains(m.SmtpMessageId)))
            .OrderByDescending(m => m.QueuedAt)
            .Select(m => (Guid?)m.EnrollmentId)
            .FirstOrDefaultAsync(ct);
        return enrollmentId is { } id ? await FindEnrollmentByIdAsync(id, ct) : null;
    }

    public async Task<IReadOnlyList<EnrollmentRef>> FindRecentByLeadEmailAsync(string email, DateTime sentSinceUtc, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();                   // lead emails are stored lowercase (LeadEmail.Normalize)
        var rows = await (from m in db.EmailMessages.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter])
                          join l in db.Leads.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]) on m.LeadId equals l.Id   // soft-deleted leads excluded
                          where l.Email == normalized && l.WorkspaceId == m.WorkspaceId && m.SentAt != null && m.SentAt >= sentSinceUtc
                          select new { m.EnrollmentId, m.WorkspaceId, m.CampaignId, m.LeadId, l.Email, m.SentAt })
            .AsNoTracking().ToListAsync(ct);
        // the latest outbound per workspace; the use case only attaches when exactly one workspace qualifies
        return rows.GroupBy(r => r.WorkspaceId)
            .Select(g => g.OrderByDescending(r => r.SentAt).First())
            .Select(r => new EnrollmentRef(r.EnrollmentId, r.WorkspaceId, r.CampaignId, r.LeadId, r.Email))
            .ToList();
    }

    public async Task<InboxThread> GetOrCreateThreadAsync(EnrollmentRef target, string subject, DateTime now, CancellationToken ct)
    {
        var existing = await FindThreadAsync(target, ct);
        if (existing is not null) return existing;

        var thread = new InboxThread
        {
            Id = Guid.NewGuid(), WorkspaceId = target.WorkspaceId, LeadId = target.LeadId, CampaignId = target.CampaignId,
            EnrollmentId = target.EnrollmentId, Subject = subject.Length <= 500 ? subject : subject[..500], LastMessageAt = now,
            IsRead = true, CreatedAt = now
        };
        db.InboxThreads.Add(thread);
        try
        {
            await db.SaveChangesAsync(ct);
            return thread;
        }
        catch (DbUpdateException)
        {
            db.Entry(thread).State = EntityState.Detached;                  // a concurrent send/reply created it first
            return await FindThreadAsync(target, ct) ?? throw new InvalidOperationException("Could not create the inbox thread.");
        }
    }

    private Task<InboxThread?> FindThreadAsync(EnrollmentRef target, CancellationToken ct)
        => db.InboxThreads.ForWorkspace(target.WorkspaceId).FirstOrDefaultAsync(t => t.EnrollmentId == target.EnrollmentId, ct);

    public async Task<bool> TryAddInboundAsync(InboxMessage message, CancellationToken ct)
    {
        var thread = await db.InboxThreads.ForWorkspace(message.WorkspaceId).FirstOrDefaultAsync(t => t.Id == message.ThreadId, ct)
                     ?? throw new InvalidOperationException("The inbox thread does not exist in this workspace.");
        if (message.OccurredAt > thread.LastMessageAt) thread.LastMessageAt = message.OccurredAt;
        thread.IsRead = false;
        thread.Classification = message.Classification;                    // denormalised: the latest inbound's classification
        db.InboxMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();                                       // drop the thread update too: a duplicate changes nothing
            // provider-agnostic: if the (WorkspaceId, RawMessageId) inbound row now exists, the dedupe index is what failed
            var exists = message.RawMessageId is not null && await db.InboxMessages.ForWorkspace(message.WorkspaceId).AsNoTracking()
                .AnyAsync(m => m.RawMessageId == message.RawMessageId && m.Direction == MessageDirection.Inbound, ct);
            if (exists) return false;
            throw;
        }
    }

    public async Task<InboundApplyResult> ApplyReplyEffectsAsync(EnrollmentRef target, bool isAutoReply, CancellationToken ct)
    {
        if (isAutoReply) return new InboundApplyResult(false, false);      // OOO/bounce: the sequence continues (PBI 25.4)

        var lead = await db.Leads.IgnoreQueryFilters(AllFilters).Where(l => l.WorkspaceId == target.WorkspaceId)
            .FirstOrDefaultAsync(l => l.Id == target.LeadId, ct);
        var enrollment = await db.CampaignEnrollments.ForWorkspace(target.WorkspaceId)
            .FirstOrDefaultAsync(e => e.Id == target.EnrollmentId, ct);

        var leadChanged = false;
        if (lead is not null && lead.Status != LeadStatus.Replied && LeadStatusRules.CanTransition(lead.Status, LeadStatus.Replied))
        {
            lead.Status = LeadStatus.Replied;
            lead.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            leadChanged = true;
        }
        var stopped = enrollment is not null && EnrollmentRules.ApplyStop(enrollment, EnrollmentRules.StopEvent.Replied);
        if (leadChanged || stopped) await db.SaveChangesAsync(ct);
        return new InboundApplyResult(leadChanged, stopped);
    }

    public async Task AddQuarantineAsync(InboundQuarantine row, CancellationToken ct)
    {
        db.InboundQuarantine.Add(row);
        await db.SaveChangesAsync(ct);
    }
}
