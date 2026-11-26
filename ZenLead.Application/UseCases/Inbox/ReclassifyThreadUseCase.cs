using ZenLead.Application.Abstractions;
using ZenLead.Application.Classification;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Inbox;

public enum ReclassifyOutcomeKind { Ok, NotFound, NothingToClassify }

public record ReclassifyOutcome(ReclassifyOutcomeKind Kind, ThreadListItem? Thread = null);

/// <summary>
/// A human corrects the classifier. Applies to the thread's latest inbound non-auto message through F26's
/// <see cref="ClassificationEffects"/> (<c>manual: true</c>), so a human "Unsubscribe" takes the same sticky actions as the AI path.
/// </summary>
public class ReclassifyThreadUseCase(IInboxRepository inbox, ClassificationEffects effects)
{
    public async Task<ReclassifyOutcome> ExecuteAsync(Guid workspaceId, Guid threadId, ReplyClassification label, CancellationToken ct)
    {
        if (await inbox.GetListItemAsync(workspaceId, threadId, ct) is null) return new(ReclassifyOutcomeKind.NotFound);
        var messageId = await inbox.FindLatestHumanInboundIdAsync(workspaceId, threadId, ct);
        if (messageId is null) return new(ReclassifyOutcomeKind.NothingToClassify);

        await effects.ApplyAsync(workspaceId, messageId.Value, label, 1m, manual: true, note: null, ct);
        return new(ReclassifyOutcomeKind.Ok, await inbox.GetListItemAsync(workspaceId, threadId, ct));
    }
}
