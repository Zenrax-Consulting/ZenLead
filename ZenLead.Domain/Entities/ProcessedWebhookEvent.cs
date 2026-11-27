namespace ZenLead.Domain.Entities;

/// <summary>
/// An Event Webhook event we have already applied (F28), keyed by SendGrid's <c>sg_event_id</c>. Not tenant-scoped on purpose:
/// events arrive before their workspace is trusted. Lets a retried or replayed batch change nothing. Housekeeping (F29) deletes
/// rows older than 30 days, comfortably past SendGrid's 24-hour retry window.
/// </summary>
public class ProcessedWebhookEvent
{
    public const int MaxIdLength = 100;

    public string SgEventId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}
