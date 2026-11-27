using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// EF persistence for the Event Webhook (F28). The endpoint is anonymous (no current workspace), so lookups ignore the tenant filter
/// and scope explicitly: messages by (workspaceId, id) pairs taken from the signed event's custom args, and everything else by the
/// loaded message's own workspace. Queries are chunked so a 500-event batch costs a handful of round trips, not hundreds.
/// </summary>
public class EmailEventStore(ZenLeadDbContext db, TimeProvider clock) : IEmailEventStore
{
    private const int InChunk = 500;
    private static readonly string[] TenantOnly = [ZenLeadDbContext.TenantFilter];
    private static readonly string[] AllFilters = [ZenLeadDbContext.TenantFilter, ZenLeadDbContext.SoftDeleteFilter];

    public async Task<IReadOnlySet<string>> FilterUnprocessedAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var fresh = new HashSet<string>(keys, StringComparer.Ordinal);
        foreach (var chunk in keys.Distinct().Chunk(InChunk))
        {
            var seen = await db.ProcessedWebhookEvents.AsNoTracking().Where(p => chunk.Contains(p.SgEventId)).Select(p => p.SgEventId).ToListAsync(ct);
            fresh.ExceptWith(seen);
        }
        return fresh;
    }

    public async Task<IReadOnlyDictionary<Guid, EmailMessage>> LoadMessagesAsync(IReadOnlyCollection<(Guid WorkspaceId, Guid MessageId)> keys, CancellationToken ct)
    {
        var result = new Dictionary<Guid, EmailMessage>();
        foreach (var chunk in keys.Select(k => k.MessageId).Distinct().Chunk(InChunk))
        {
            var rows = await db.EmailMessages.IgnoreQueryFilters(TenantOnly).Where(m => chunk.Contains(m.Id)).ToListAsync(ct);
            foreach (var m in rows) result[m.Id] = m;
        }
        // keep a message only if some event claimed ITS workspace; the use case re-checks per event
        var claimed = keys.ToHashSet();
        foreach (var id in result.Keys.ToList())
            if (!claimed.Contains((result[id].WorkspaceId, id)))
            {
                db.Entry(result[id]).State = EntityState.Detached;
                result.Remove(id);
            }
        return result;
    }

    public async Task<EventEffectTargets> LoadEffectTargetsAsync(IReadOnlyCollection<EmailMessage> messages, CancellationToken ct)
    {
        var targets = new EventEffectTargets();
        var workspaceOf = messages.GroupBy(m => m.LeadId).ToDictionary(g => g.Key, g => g.First().WorkspaceId);

        foreach (var chunk in messages.Select(m => m.EnrollmentId).Distinct().Chunk(InChunk))
            foreach (var e in await db.CampaignEnrollments.IgnoreQueryFilters(TenantOnly).Where(e => chunk.Contains(e.Id)).ToListAsync(ct))
                targets.Enrollments[e.Id] = e;

        foreach (var chunk in workspaceOf.Keys.Chunk(InChunk))
        {
            foreach (var l in await db.Leads.IgnoreQueryFilters(AllFilters).Where(l => chunk.Contains(l.Id)).ToListAsync(ct))
                if (workspaceOf[l.Id] == l.WorkspaceId) targets.Leads[l.Id] = l;

            var live = await db.CampaignEnrollments.IgnoreQueryFilters(TenantOnly)
                .Where(e => chunk.Contains(e.LeadId) && (e.Status == EnrollmentStatus.Active || e.Status == EnrollmentStatus.Paused))
                .ToListAsync(ct);
            foreach (var e in live.Where(e => workspaceOf[e.LeadId] == e.WorkspaceId))
            {
                if (!targets.LiveEnrollmentsByLead.TryGetValue(e.LeadId, out var list)) targets.LiveEnrollmentsByLead[e.LeadId] = list = [];
                list.Add(e);
            }
        }

        // drop anything whose workspace doesn't match the message that led us to it (ids are ours, but stay strict)
        foreach (var m in messages)
            if (targets.Enrollments.TryGetValue(m.EnrollmentId, out var e) && e.WorkspaceId != m.WorkspaceId) targets.Enrollments.Remove(m.EnrollmentId);

        var emails = targets.Leads.Values.Select(l => l.Email.Trim().ToLowerInvariant()).Distinct().ToList();
        foreach (var chunk in emails.Chunk(InChunk))
        {
            var rows = await db.SuppressedEmails.IgnoreQueryFilters(TenantOnly).AsNoTracking().Where(s => chunk.Contains(s.Email))
                .Select(s => new { s.WorkspaceId, s.Email }).ToListAsync(ct);
            foreach (var s in rows) targets.Suppressed.Add(EventEffectTargets.SuppressionKey(s.WorkspaceId, s.Email));
        }
        return targets;
    }

    public void StageSuppression(SuppressedEmail row) => db.SuppressedEmails.Add(row);

    public async Task<bool> CommitAsync(IReadOnlyCollection<string> processedKeys, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var key in processedKeys.Distinct()) db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { SgEventId = key, ReceivedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);                                      // one SaveChanges = one transaction: state + processed ids together
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // a concurrent delivery of the same events won the primary-key race: everything it applied is already in
            var keys = processedKeys.Distinct().ToList();
            var existing = 0;
            foreach (var chunk in keys.Chunk(InChunk))
                existing += await db.ProcessedWebhookEvents.AsNoTracking().CountAsync(p => chunk.Contains(p.SgEventId), ct);
            if (existing > 0) return false;
            throw;
        }
    }
}
