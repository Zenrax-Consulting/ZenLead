using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Application.Email;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.UseCases.Inbox;

public enum ReplyOutcomeKind { Sent, Duplicate, Failed, Blocked, NotFound }

/// <summary>The controller maps Sent → 201, Duplicate → 200, Failed → 502 send_failed, Blocked → 409 suppressed, NotFound → 404.</summary>
public record ReplyOutcome(ReplyOutcomeKind Kind, MessageDto? Message = null, string? Error = null)
{
    public static readonly ReplyOutcome NotFound = new(ReplyOutcomeKind.NotFound);
    public static ReplyOutcome Sent(InboxMessage m) => new(ReplyOutcomeKind.Sent, InboxMapping.ToDto(m));
    public static ReplyOutcome Failed(InboxMessage m) => new(ReplyOutcomeKind.Failed, InboxMapping.ToDto(m), m.DeliveryError);
    public static ReplyOutcome Blocked(string reason) => new(ReplyOutcomeKind.Blocked, Error: reason);
    public static ReplyOutcome Duplicate(InboxMessage? m) => m is null ? NotFound : new(ReplyOutcomeKind.Duplicate, InboxMapping.ToDto(m));
}

/// <summary>
/// A one-to-one answer to someone who wrote to us. Same safety rules as campaign mail: suppressed leads are blocked, the row is inserted
/// <b>before</b> the send (idempotent on the browser's request id, so a double click can't send twice), and a failed send is recorded.
/// No unsubscribe footer and no open tracking: the suppression check is the compliance guard.
/// </summary>
public class SendInboxReplyUseCase(
    IInboxRepository inbox, ISuppressionList suppression, IEmailSender sender, IOptions<EmailOptions> email, TimeProvider clock)
{
    public const int MaxErrorLength = 500;

    public async Task<ReplyOutcome> ExecuteAsync(Guid workspaceId, Guid userId, Guid threadId, ReplyRequest request, CancellationToken ct)
    {
        var target = await inbox.GetThreadForReplyAsync(workspaceId, threadId, ct);
        if (target is null) return ReplyOutcome.NotFound;

        if (await inbox.FindByClientRequestAsync(workspaceId, threadId, request.ClientRequestId, ct) is { } already)
            return ReplyOutcome.Duplicate(already);                                       // double click / retry: return what was recorded

        var (thread, lead) = target;
        var reason = InboxMapping.CannotReplyReason(lead, await suppression.ContainsAsync(workspaceId, lead.Email, ct));
        if (reason is not null) return ReplyOutcome.Blocked(reason);

        var options = email.Value;
        var subject = InboxMapping.ReplySubject(thread.Subject);
        if (subject.Length > 500) subject = subject[..500];
        var now = clock.GetUtcNow().UtcDateTime;
        var message = new InboxMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, ThreadId = threadId, Direction = MessageDirection.Outbound,
            FromAddress = options.FromAddress, FromName = options.FromName, ToAddress = lead.Email, Subject = subject,
            TextBody = request.Body.Trim(), OccurredAt = now, ClientRequestId = request.ClientRequestId,
            DeliveryStatus = OutboundDeliveryStatus.Pending, SentByUserId = userId
        };
        if (!await inbox.TryAddOutboundPendingAsync(message, ct))                          // lost the double-click race: the other request sends
            return ReplyOutcome.Duplicate(await inbox.FindByClientRequestAsync(workspaceId, threadId, request.ClientRequestId, ct));

        var lastInbound = await inbox.FindLatestInboundMessageIdAsync(workspaceId, threadId, ct);
        var refs = await inbox.GetReferencesAsync(workspaceId, threadId, ct);
        var headers = new Dictionary<string, string>();
        if (lastInbound is not null) headers["In-Reply-To"] = $"<{lastInbound}>";
        if (refs.Count > 0) headers["References"] = string.Join(' ', refs.Select(r => $"<{r}>"));

        SendResult result;
        try
        {
            result = await sender.SendAsync(new OutboundEmail(
                new EmailAddress(lead.Email, lead.Name), subject, message.TextBody, HtmlBody: null,
                ReplyTo: thread.EnrollmentId is { } e ? new EmailAddress(ReplyAddress.Encode(e, options.InboundDomain)) : null,
                Headers: headers,
                CustomArgs: new Dictionary<string, string> { ["inboxMessageId"] = message.Id.ToString("N"), ["workspaceId"] = workspaceId.ToString("N") },
                Categories: ["inbox-reply"], TrackOpens: false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = SendResult.Fail(ex.Message, retryable: true);                       // never leave the row Pending on an unexpected error
        }

        message.DeliveryStatus = result.Success ? OutboundDeliveryStatus.Sent : OutboundDeliveryStatus.Failed;
        message.DeliveryError = result.Success ? null : Truncate(result.Error ?? "The email could not be sent.", MaxErrorLength);
        if (result.Success)
        {
            if (now > thread.LastMessageAt) thread.LastMessageAt = now;
            thread.IsRead = true;
        }
        await inbox.SaveAsync(ct);
        return result.Success ? ReplyOutcome.Sent(message) : ReplyOutcome.Failed(message);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
