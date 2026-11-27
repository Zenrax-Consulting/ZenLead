using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Sending;

/// <summary>The SendGrid Event Webhook events F28 acts on; everything else (processed, deferred, click, ...) maps to <see cref="Ignored"/>.</summary>
public enum EmailEventType { Delivered, Open, Bounce, Blocked, Dropped, SpamReport, Unsubscribe, Ignored }

/// <param name="Reason">SendGrid's free-text reason (bounce/blocked/dropped). Never contains our message body.</param>
/// <param name="Status">The SMTP enhanced status code for bounces, e.g. <c>5.1.1</c>.</param>
/// <param name="SmtpId">The RFC Message-ID SendGrid generated (<c>smtp-id</c>), usually in angle brackets.</param>
public record EmailEvent(EmailEventType Type, DateTime OccurredAtUtc, string? Reason = null, string? Status = null, string? SmtpId = null);

/// <summary>What else should happen because of an event (applied by the application layer: enrollment, lead, suppression list).</summary>
public enum EventEffect { None, HardBounce, SoftStop, SpamReport, Unsubscribe }

/// <summary>
/// Applies one delivery event to an <see cref="EmailMessage"/>. Order-independent by design, because SendGrid delivers events
/// out of order and across batches: timestamps keep the <b>earliest</b> value seen (so "delivered then open" and "open then
/// delivered" end identically), status only moves forward (<c>Queued &lt; Sent &lt; Delivered</c>), and <c>Bounced</c>/<c>Dropped</c>
/// are terminal yet may follow <c>Delivered</c> (asynchronous bounces exist). An open proves delivery.
/// </summary>
public static class EmailEventRules
{
    public const int MaxErrorLength = 500;

    private static readonly string[] SuppressedAddressDropReasons = ["Bounced Address", "Spam Reporting Address", "Unsubscribed Address", "Invalid"];

    public static EventEffect Apply(EmailMessage m, EmailEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.SmtpId) && m.SmtpMessageId is null)
            m.SmtpMessageId = Truncate(e.SmtpId.Trim().Trim('<', '>'), 500);     // the real RFC Message-ID: lets F25 match In-Reply-To headers

        switch (e.Type)
        {
            case EmailEventType.Delivered:
                m.DeliveredAt = Earliest(m.DeliveredAt, e.OccurredAtUtc);
                PromoteToDelivered(m);                                            // never downgrades Bounced/Dropped/Failed
                return EventEffect.None;

            case EmailEventType.Open:
                m.OpenedAt = Earliest(m.OpenedAt, e.OccurredAtUtc);               // first open only: "unique opens"
                m.DeliveredAt = Earliest(m.DeliveredAt, e.OccurredAtUtc);         // an open proves delivery even if the delivered event is late/lost
                PromoteToDelivered(m);
                return EventEffect.None;

            case EmailEventType.Bounce:
                m.BouncedAt = Earliest(m.BouncedAt, e.OccurredAtUtc);
                m.Status = EmailMessageStatus.Bounced;
                m.Error = Truncate(e.Reason, MaxErrorLength);
                return IsHard(e.Status) ? EventEffect.HardBounce : EventEffect.SoftStop;

            case EmailEventType.Blocked:                                          // recipient server refused (reputation/policy): the address may be fine
                m.BouncedAt = Earliest(m.BouncedAt, e.OccurredAtUtc);
                m.Status = EmailMessageStatus.Bounced;
                m.Error = Truncate(e.Reason, MaxErrorLength);
                return EventEffect.SoftStop;

            case EmailEventType.Dropped:                                          // SendGrid refused to send it at all
                if (m.Status != EmailMessageStatus.Bounced) m.Status = EmailMessageStatus.Dropped;
                m.Error = Truncate(e.Reason, MaxErrorLength);
                return DroppedForSuppressedAddress(e.Reason) ? EventEffect.HardBounce : EventEffect.SoftStop;

            case EmailEventType.SpamReport:
                return EventEffect.SpamReport;

            case EmailEventType.Unsubscribe:
                return EventEffect.Unsubscribe;

            default:
                return EventEffect.None;
        }
    }

    /// <summary>5.x.x is permanent (hard); anything else SendGrid reports (4.x.x) is transient. No status at all is treated as hard: safer for sender reputation.</summary>
    public static bool IsHard(string? status) => string.IsNullOrWhiteSpace(status) || status.TrimStart().StartsWith('5');

    public static bool DroppedForSuppressedAddress(string? reason)
        => reason is not null && SuppressedAddressDropReasons.Any(k => reason.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static void PromoteToDelivered(EmailMessage m)
    {
        if (m.Status is EmailMessageStatus.Queued or EmailMessageStatus.Sent) m.Status = EmailMessageStatus.Delivered;
    }

    private static DateTime Earliest(DateTime? current, DateTime candidate) => current is { } c && c <= candidate ? c : candidate;

    private static string? Truncate(string? v, int max) => v is null ? null : v.Length <= max ? v : v[..max];
}
