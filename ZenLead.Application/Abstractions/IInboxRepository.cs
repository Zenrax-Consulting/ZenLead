using ZenLead.Application.Dtos.Inbox;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

/// <summary>A thread with what the conversation view needs. <see cref="Lead"/> may be soft-deleted (history stays readable).</summary>
public record InboxThreadView(InboxThread Thread, Lead Lead, string? CampaignName, IReadOnlyList<InboxMessage> Messages);

/// <summary>A tracked thread and its (possibly soft-deleted) lead, for sending a reply.</summary>
public record InboxReplyTarget(InboxThread Thread, Lead Lead);

/// <summary>
/// Inbox reads and writes (F27). Every method is scoped to the caller's workspace twice — the ambient tenant filter and an explicit
/// <c>WorkspaceId</c> check — so another workspace's thread is simply "not found".
/// </summary>
public interface IInboxRepository
{
    /// <summary>Threads with at least one inbound message, newest first: one page query plus the count, no per-row queries.</summary>
    Task<PagedResult<ThreadListItem>> SearchThreadsAsync(Guid workspaceId, ThreadListQuery query, CancellationToken ct);
    Task<ThreadListItem?> GetListItemAsync(Guid workspaceId, Guid threadId, CancellationToken ct);
    /// <summary>The thread, its lead and its messages ordered by <c>OccurredAt</c>.</summary>
    Task<InboxThreadView?> GetThreadAsync(Guid workspaceId, Guid threadId, CancellationToken ct);
    /// <summary>A single UPDATE; false when the thread does not exist in the workspace.</summary>
    Task<bool> MarkReadAsync(Guid workspaceId, Guid threadId, bool isRead, CancellationToken ct);
    Task<int> UnreadCountAsync(Guid workspaceId, CancellationToken ct);

    Task<InboxReplyTarget?> GetThreadForReplyAsync(Guid workspaceId, Guid threadId, CancellationToken ct);
    Task<InboxMessage?> FindByClientRequestAsync(Guid workspaceId, Guid threadId, Guid clientRequestId, CancellationToken ct);
    /// <summary>The Message-ID of the latest inbound message, or null.</summary>
    Task<string?> FindLatestInboundMessageIdAsync(Guid workspaceId, Guid threadId, CancellationToken ct);
    /// <summary>The last (at most 10) known Message-IDs in the thread, oldest first.</summary>
    Task<IReadOnlyList<string>> GetReferencesAsync(Guid workspaceId, Guid threadId, CancellationToken ct);
    /// <summary>The latest inbound message that is not an auto-reply (the one a human reclassifies), or null.</summary>
    Task<Guid?> FindLatestHumanInboundIdAsync(Guid workspaceId, Guid threadId, CancellationToken ct);
    /// <summary>Inserts and saves a Pending outbound reply; false when a row with the same (ThreadId, ClientRequestId) exists.</summary>
    Task<bool> TryAddOutboundPendingAsync(InboxMessage message, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}
