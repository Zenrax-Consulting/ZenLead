using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Inbox;

namespace ZenLead.Application.UseCases.Inbox;

/// <summary>
/// Opens a conversation. Reading marks the thread read (parent plan): a side effect on GET is acceptable because the endpoint is
/// authenticated (no link-prefetch risk) and it is idempotent; "mark unread" is explicit.
/// </summary>
public class GetInboxThreadUseCase(IInboxRepository inbox, ISuppressionList suppression)
{
    public async Task<ThreadDetail?> ExecuteAsync(Guid workspaceId, Guid threadId, CancellationToken ct)
    {
        var view = await inbox.GetThreadAsync(workspaceId, threadId, ct);
        if (view is null) return null;
        if (!view.Thread.IsRead) await inbox.MarkReadAsync(workspaceId, threadId, true, ct);

        var lead = view.Lead;
        var reason = InboxMapping.CannotReplyReason(lead, await suppression.ContainsAsync(workspaceId, lead.Email, ct));
        var t = view.Thread;
        return new ThreadDetail(t.Id, lead.Id, lead.Name, lead.Email, lead.Title, lead.Company?.Name, lead.Status,
            t.CampaignId, view.CampaignName, t.Subject, t.Classification, reason is null, reason,
            view.Messages.Select(InboxMapping.ToDto).ToList());
    }
}
