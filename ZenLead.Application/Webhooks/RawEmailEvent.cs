using System.Text.Json.Serialization;

namespace ZenLead.Application.Webhooks;

/// <summary>
/// One element of a SendGrid Event Webhook post, exactly as sent (SendGrid's names are snake_case / kebab-case). Unknown fields
/// are ignored. Our custom args (<c>emailMessageId</c>, <c>workspaceId</c>, <c>campaignId</c>) arrive as top-level strings and are
/// parsed leniently: a malformed value makes the event "unknown", never the whole batch a 400.
/// </summary>
public class RawEmailEvent
{
    [JsonPropertyName("email")] public string? Email { get; set; }

    /// <summary>Unix seconds.</summary>
    [JsonPropertyName("timestamp")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Timestamp { get; set; }

    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("sg_event_id")] public string? SgEventId { get; set; }
    [JsonPropertyName("sg_message_id")] public string? SgMessageId { get; set; }
    [JsonPropertyName("smtp-id")] public string? SmtpId { get; set; }

    /// <summary>For <c>bounce</c> events: <c>bounce</c> or <c>blocked</c>.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }

    /// <summary>SMTP enhanced status code, e.g. <c>5.1.1</c>.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    [JsonPropertyName("emailMessageId")] public string? EmailMessageIdArg { get; set; }
    [JsonPropertyName("workspaceId")] public string? WorkspaceIdArg { get; set; }
    [JsonPropertyName("campaignId")] public string? CampaignIdArg { get; set; }

    [JsonIgnore] public Guid? EmailMessageId => Guid.TryParse(EmailMessageIdArg, out var id) && id != Guid.Empty ? id : null;
    [JsonIgnore] public Guid? WorkspaceId => Guid.TryParse(WorkspaceIdArg, out var id) && id != Guid.Empty ? id : null;

    [JsonIgnore]
    public DateTime OccurredAtUtc => Timestamp is > 0 and < 253_402_300_799 ? DateTimeOffset.FromUnixTimeSeconds(Timestamp).UtcDateTime : DateTime.UnixEpoch;
}
