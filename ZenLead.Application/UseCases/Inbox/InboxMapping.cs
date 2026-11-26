using ZenLead.Application.Dtos.Inbox;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;

namespace ZenLead.Application.UseCases.Inbox;

public static class InboxMapping
{
    public const string SuppressedReason = "This contact has unsubscribed or bounced, so you can't email them.";
    public const string DeletedReason = "This lead was deleted, so you can't email them.";

    public static MessageDto ToDto(InboxMessage m) => new(
        m.Id, m.Direction, m.FromAddress, m.FromName, m.Subject, m.TextBody,
        string.IsNullOrEmpty(m.FullTextBody) || m.FullTextBody.Trim() == m.TextBody.Trim() ? null : m.FullTextBody,
        m.OccurredAt, m.Classification, m.ClassificationConfidence, m.ClassifiedBy, m.NeedsReview, m.SenderMismatch, m.IsAutoReply,
        m.DeliveryStatus, m.DeliveryError);

    /// <summary>Why this lead can't be emailed from the inbox, or null when a reply is allowed.</summary>
    public static string? CannotReplyReason(Lead lead, bool onSuppressionList)
        => lead.DeletedAt is not null ? DeletedReason
         : LeadStatusRules.IsSuppressed(lead.Status) || onSuppressionList ? SuppressedReason
         : null;

    /// <summary>"Re: subject", never "Re: Re: subject".</summary>
    public static string ReplySubject(string threadSubject)
    {
        var subject = threadSubject.Trim();
        return subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";
    }

    internal static bool IsHumanInbound(InboxMessage m) => m.Direction == MessageDirection.Inbound && !m.IsAutoReply;
}
