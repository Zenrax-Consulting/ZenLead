using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Inbox;

public record ThreadListQuery(int Page = 1, int PageSize = 25, ReplyClassification? Classification = null, bool? Unread = null,
                              bool? NeedsReview = null, Guid? CampaignId = null, string? Q = null);

public record ThreadListItem(Guid Id, Guid LeadId, string LeadName, string LeadEmail, string? CompanyName,
    Guid? CampaignId, string? CampaignName, string Subject, string Snippet, DateTime LastMessageAt,
    bool IsRead, ReplyClassification Classification, bool NeedsReview, int MessageCount);

public record MessageDto(Guid Id, MessageDirection Direction, string FromAddress, string? FromName, string Subject, string TextBody,
    string? FullTextBody,                                // only when it differs from TextBody (quoted history was stripped)
    DateTime OccurredAt, ReplyClassification Classification, decimal? ClassificationConfidence, ClassificationSource ClassifiedBy,
    bool NeedsReview, bool SenderMismatch, bool IsAutoReply, OutboundDeliveryStatus? DeliveryStatus, string? DeliveryError);

public record ThreadDetail(Guid Id, Guid LeadId, string LeadName, string LeadEmail, string? LeadTitle, string? CompanyName, LeadStatus LeadStatus,
    Guid? CampaignId, string? CampaignName, string Subject, ReplyClassification Classification, bool CanReply, string? CannotReplyReason,
    IReadOnlyList<MessageDto> Messages);

public record ReplyRequest(Guid ClientRequestId, string Body);
public record ReclassifyRequest(ReplyClassification Classification);
public record SetReadRequest(bool IsRead);
public record UnreadCountResponse(int Count);
