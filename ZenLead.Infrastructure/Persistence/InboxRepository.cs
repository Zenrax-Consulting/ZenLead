using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

/// <summary>
/// EF persistence for the inbox API (F27). Runs in HTTP requests, so the ambient tenant filter applies; every query also checks the
/// workspace explicitly. Leads are read with the soft-delete filter off (a deleted lead's conversation stays readable), never the tenant one.
/// </summary>
public class InboxRepository(ZenLeadDbContext db) : IInboxRepository
{
    public const int SnippetLength = 160;
    private const int MaxReferences = 10;

    private IQueryable<Lead> LeadsIncludingDeleted => db.Leads.IgnoreQueryFilters([ZenLeadDbContext.SoftDeleteFilter]);

    private IQueryable<InboxThread> Threads(Guid workspaceId) => db.InboxThreads.Where(t => t.WorkspaceId == workspaceId);

    private IQueryable<InboxMessage> Messages(Guid workspaceId, Guid threadId)
        => db.InboxMessages.Where(m => m.WorkspaceId == workspaceId && m.ThreadId == threadId);

    // member-init (not a positional record) so EF can keep composing Where/OrderBy over it
    private sealed class Row
    {
        public required InboxThread T { get; init; }
        public required Lead L { get; init; }
        public string? CampaignName { get; init; }
    }

    /// <summary>Threads someone has replied to (F23 creates a thread per sent enrollment; the inbox shows conversations, not the outbox).</summary>
    private IQueryable<Row> Rows(Guid workspaceId)
        => from t in Threads(workspaceId).AsNoTracking()
           where db.InboxMessages.Any(m => m.ThreadId == t.Id && m.Direction == MessageDirection.Inbound)
           join l in LeadsIncludingDeleted.AsNoTracking() on t.LeadId equals l.Id
           join c in db.Campaigns.AsNoTracking() on t.CampaignId equals c.Id into cs
           from c in cs.DefaultIfEmpty()
           select new Row { T = t, L = l, CampaignName = c != null ? c.Name : null };

    /// <summary>One SQL statement per page: the snippet, review flag and count are correlated scalar subqueries (no N+1, no APPLY).</summary>
    private IQueryable<ThreadListItem> Project(IQueryable<Row> rows)
        => rows.Select(r => new ThreadListItem(
            r.T.Id, r.T.LeadId, r.L.Name, r.L.Email, r.L.Company != null ? r.L.Company.Name : null,
            r.T.CampaignId, r.CampaignName, r.T.Subject,
            db.InboxMessages.Where(m => m.ThreadId == r.T.Id)
                .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Direction)
                .Select(m => m.TextBody.Substring(0, SnippetLength)).FirstOrDefault() ?? "",
            r.T.LastMessageAt, r.T.IsRead, r.T.Classification,
            db.InboxMessages.Where(m => m.ThreadId == r.T.Id && m.Direction == MessageDirection.Inbound)
                .OrderByDescending(m => m.OccurredAt).Select(m => m.NeedsReview).FirstOrDefault(),
            db.InboxMessages.Count(m => m.ThreadId == r.T.Id)));

    public async Task<PagedResult<ThreadListItem>> SearchThreadsAsync(Guid workspaceId, ThreadListQuery q, CancellationToken ct)
    {
        var rows = Rows(workspaceId);
        if (q.Classification is { } classification) rows = rows.Where(r => r.T.Classification == classification);
        if (q.Unread is { } unread) rows = rows.Where(r => r.T.IsRead != unread);
        if (q.CampaignId is { } campaignId) rows = rows.Where(r => r.T.CampaignId == campaignId);
        if (q.NeedsReview is { } needsReview)
            rows = rows.Where(r => db.InboxMessages.Where(m => m.ThreadId == r.T.Id && m.Direction == MessageDirection.Inbound)
                .OrderByDescending(m => m.OccurredAt).Select(m => m.NeedsReview).FirstOrDefault() == needsReview);
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var like = $"%{EscapeLike(q.Q.Trim())}%";
            rows = rows.Where(r => EF.Functions.Like(r.L.Name, like, "\\") || EF.Functions.Like(r.L.Email, like, "\\")
                                || EF.Functions.Like(r.T.Subject, like, "\\"));
        }

        var total = await rows.CountAsync(ct);
        var items = await Project(rows.OrderByDescending(r => r.T.LastMessageAt).ThenByDescending(r => r.T.Id)
                .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize))
            .ToListAsync(ct);
        return new PagedResult<ThreadListItem>(items.Select(Tidy).ToList(), total, q.Page, q.PageSize);
    }

    public async Task<ThreadListItem?> GetListItemAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
    {
        var item = await Project(Rows(workspaceId).Where(r => r.T.Id == threadId)).FirstOrDefaultAsync(ct);
        return item is null ? null : Tidy(item);
    }

    /// <summary>One line for the list: whitespace (newlines included) collapsed.</summary>
    private static ThreadListItem Tidy(ThreadListItem i)
        => i with { Snippet = string.Join(' ', i.Snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) };

    public async Task<InboxThreadView?> GetThreadAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
    {
        var thread = await Threads(workspaceId).AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null) return null;
        var lead = await LeadsIncludingDeleted.AsNoTracking().Include(l => l.Company)
            .FirstOrDefaultAsync(l => l.Id == thread.LeadId && l.WorkspaceId == workspaceId, ct);
        if (lead is null) return null;
        var campaignName = thread.CampaignId is { } cid
            ? await db.Campaigns.AsNoTracking().Where(c => c.Id == cid && c.WorkspaceId == workspaceId).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        var messages = await Messages(workspaceId, threadId).AsNoTracking()
            .OrderBy(m => m.OccurredAt).ThenBy(m => m.Direction == MessageDirection.Inbound ? 1 : 0).ToListAsync(ct);
        return new InboxThreadView(thread, lead, campaignName, messages);
    }

    public async Task<bool> MarkReadAsync(Guid workspaceId, Guid threadId, bool isRead, CancellationToken ct)
        => await Threads(workspaceId).Where(t => t.Id == threadId).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRead, isRead), ct) > 0;

    public Task<int> UnreadCountAsync(Guid workspaceId, CancellationToken ct)
        => Rows(workspaceId).CountAsync(r => !r.T.IsRead, ct);

    public async Task<InboxReplyTarget?> GetThreadForReplyAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
    {
        var thread = await Threads(workspaceId).FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null) return null;
        var lead = await LeadsIncludingDeleted.AsNoTracking().FirstOrDefaultAsync(l => l.Id == thread.LeadId && l.WorkspaceId == workspaceId, ct);
        return lead is null ? null : new InboxReplyTarget(thread, lead);
    }

    public Task<InboxMessage?> FindByClientRequestAsync(Guid workspaceId, Guid threadId, Guid clientRequestId, CancellationToken ct)
        => Messages(workspaceId, threadId).AsNoTracking().FirstOrDefaultAsync(m => m.ClientRequestId == clientRequestId, ct);

    public Task<string?> FindLatestInboundMessageIdAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
        => Messages(workspaceId, threadId).Where(m => m.Direction == MessageDirection.Inbound && m.RawMessageId != null)
            .OrderByDescending(m => m.OccurredAt).Select(m => m.RawMessageId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<string>> GetReferencesAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
    {
        var latestFirst = await Messages(workspaceId, threadId).Where(m => m.RawMessageId != null)
            .OrderByDescending(m => m.OccurredAt).Select(m => m.RawMessageId!).Take(MaxReferences).ToListAsync(ct);
        latestFirst.Reverse();
        return latestFirst.Distinct().ToList();
    }

    public Task<Guid?> FindLatestHumanInboundIdAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
        => Messages(workspaceId, threadId).Where(m => m.Direction == MessageDirection.Inbound && !m.IsAutoReply)
            .OrderByDescending(m => m.OccurredAt).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryAddOutboundPendingAsync(InboxMessage message, CancellationToken ct)
    {
        db.InboxMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(message).State = EntityState.Detached;
            // provider-agnostic: if the (ThreadId, ClientRequestId) row now exists, the idempotency index is what failed
            var exists = await Messages(message.WorkspaceId, message.ThreadId).AsNoTracking()
                .AnyAsync(m => m.ClientRequestId == message.ClientRequestId, ct);
            if (exists) return false;
            throw;
        }
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
