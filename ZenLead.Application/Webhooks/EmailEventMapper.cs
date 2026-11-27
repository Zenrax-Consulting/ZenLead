using System.Security.Cryptography;
using System.Text;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Sending;

namespace ZenLead.Application.Webhooks;

public static class EmailEventMapper
{
    /// <summary>
    /// SendGrid event names → our types. <c>bounce</c> carries <c>type</c> = <c>bounce</c> | <c>blocked</c>; both unsubscribe flavours
    /// map to <see cref="EmailEventType.Unsubscribe"/>. Everything else (processed, deferred, click, group_resubscribe, ...) is Ignored.
    /// </summary>
    public static EmailEvent Map(RawEmailEvent raw)
    {
        var type = raw.Event?.Trim().ToLowerInvariant() switch
        {
            "delivered" => EmailEventType.Delivered,
            "open" => EmailEventType.Open,
            "bounce" => string.Equals(raw.Type?.Trim(), "blocked", StringComparison.OrdinalIgnoreCase) ? EmailEventType.Blocked : EmailEventType.Bounce,
            "blocked" => EmailEventType.Blocked,
            "dropped" => EmailEventType.Dropped,
            "spamreport" => EmailEventType.SpamReport,
            "unsubscribe" or "group_unsubscribe" => EmailEventType.Unsubscribe,
            _ => EmailEventType.Ignored
        };
        return new EmailEvent(type, raw.OccurredAtUtc, raw.Reason, raw.Status, raw.SmtpId);
    }

    /// <summary>
    /// The idempotency key: <c>sg_event_id</c> when present (and short enough to store); otherwise a stable hash of
    /// (sg_message_id, event, timestamp, email) — the fields a SendGrid retry repeats verbatim.
    /// </summary>
    public static string IdempotencyKey(RawEmailEvent raw)
    {
        var id = raw.SgEventId?.Trim();
        if (!string.IsNullOrEmpty(id) && id.Length <= ProcessedWebhookEvent.MaxIdLength) return id;
        var input = string.IsNullOrEmpty(id) ? $"{raw.SgMessageId}|{raw.Event}|{raw.Timestamp}|{raw.Email}" : id;
        return "syn:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
