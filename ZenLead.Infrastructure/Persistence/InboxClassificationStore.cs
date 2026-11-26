using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// EF persistence for reply classification (F26) and the manual override (F27). Runs in a Hangfire job with no current workspace:
/// every query is explicitly scoped with <see cref="TenantQueryExtensions.ForWorkspace{T}"/>, so a message id from another workspace
/// loads nothing. Soft-deleted leads are still loaded — an unsubscribe must still be honoured for them.
/// </summary>
public class InboxClassificationStore(ZenLeadDbContext db, ICampaignEnrollmentRepository enrollments, ILogger<InboxClassificationStore> logger) : IInboxClassificationStore
{
    private static readonly string[] AllFilters = [ZenLeadDbContext.TenantFilter, ZenLeadDbContext.SoftDeleteFilter];

    public async Task<ClassificationContext?> LoadAsync(Guid workspaceId, Guid messageId, CancellationToken ct)
    {
        var message = await db.InboxMessages.ForWorkspace(workspaceId).FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return null;
        var thread = await db.InboxThreads.ForWorkspace(workspaceId).FirstOrDefaultAsync(t => t.Id == message.ThreadId, ct);
        if (thread is null) return null;
        var lead = await db.Leads.IgnoreQueryFilters(AllFilters).Where(l => l.WorkspaceId == workspaceId).FirstOrDefaultAsync(l => l.Id == thread.LeadId, ct);
        if (lead is null) return null;
        var enrollment = thread.EnrollmentId is { } eid
            ? await db.CampaignEnrollments.ForWorkspace(workspaceId).FirstOrDefaultAsync(e => e.Id == eid, ct)
            : null;

        var campaignId = enrollment?.CampaignId ?? thread.CampaignId;
        var campaign = campaignId is { } cid
            ? await db.Campaigns.ForWorkspace(workspaceId).AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;
        var workspaceZone = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.TimeZone).FirstOrDefaultAsync(ct) ?? "UTC";
        var window = campaign is not null
            ? CampaignWindowFactory.For(campaign, workspaceZone, logger)
            : new SendWindow(TimeZoneInfo.Utc, new TimeOnly(9, 0), new TimeOnly(17, 0), SendDays.Weekdays);

        var newerInbound = await db.InboxMessages.ForWorkspace(workspaceId).AnyAsync(m => m.ThreadId == thread.Id && m.Id != message.Id
            && m.Direction == MessageDirection.Inbound && !m.IsAutoReply && m.OccurredAt > message.OccurredAt, ct);
        return new ClassificationContext(message, thread, enrollment, lead, window, IsLatestInbound: !newerInbound);
    }

    public async Task<ClassificationInput?> LoadForClassificationAsync(Guid workspaceId, Guid messageId, CancellationToken ct)
    {
        var message = await db.InboxMessages.ForWorkspace(workspaceId).AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return null;
        var thread = await db.InboxThreads.ForWorkspace(workspaceId).AsNoTracking()
            .Where(t => t.Id == message.ThreadId).Select(t => new { t.CampaignId, t.EnrollmentId }).FirstOrDefaultAsync(ct);

        // what the lead replied to: the latest outbound at or before the reply (else the latest at all)
        var outbound = db.InboxMessages.ForWorkspace(workspaceId).AsNoTracking()
            .Where(m => m.ThreadId == message.ThreadId && m.Direction == MessageDirection.Outbound);
        var original = await outbound.Where(m => m.OccurredAt <= message.OccurredAt).OrderByDescending(m => m.OccurredAt)
                           .Select(m => new { m.Subject, m.TextBody }).FirstOrDefaultAsync(ct)
                       ?? await outbound.OrderByDescending(m => m.OccurredAt).Select(m => new { m.Subject, m.TextBody }).FirstOrDefaultAsync(ct);
        return new ClassificationInput(message, original?.Subject, original?.TextBody, thread?.CampaignId, thread?.EnrollmentId);
    }

    public async Task MarkUnclassifiedAsync(Guid workspaceId, Guid messageId, string note, CancellationToken ct)
    {
        var message = await db.InboxMessages.ForWorkspace(workspaceId).FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return;
        message.Classification = ReplyClassification.Unclassified;
        message.ClassificationConfidence = null;
        message.ClassificationNote = note.Length <= 100 ? note : note[..100];
        await db.SaveChangesAsync(ct);
    }

    public Task<int> StopAllEnrollmentsForLeadAsync(Guid workspaceId, Guid leadId, EnrollmentRules.StopEvent stop, CancellationToken ct)
        => enrollments.StopAllForLeadAsync(workspaceId, leadId, stop, ct);           // same rule as F23's public unsubscribe

    public async Task<bool> ThreadHasOtherHumanReplyAsync(Guid threadId, Guid messageId, CancellationToken ct)
    {
        var workspaceId = await db.InboxThreads.IgnoreQueryFilters([ZenLeadDbContext.TenantFilter]).Where(t => t.Id == threadId)
            .Select(t => (Guid?)t.WorkspaceId).FirstOrDefaultAsync(ct);
        if (workspaceId is null) return false;
        return await db.InboxMessages.ForWorkspace(workspaceId.Value).AnyAsync(m => m.ThreadId == threadId && m.Id != messageId
            && m.Direction == MessageDirection.Inbound && !m.IsAutoReply
            && m.Classification != ReplyClassification.OutOfOffice && m.Classification != ReplyClassification.Bounce, ct);
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
