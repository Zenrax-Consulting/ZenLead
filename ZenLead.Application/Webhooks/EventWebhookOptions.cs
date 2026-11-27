namespace ZenLead.Application.Webhooks;

/// <summary>
/// The signed SendGrid Event Webhook (F28), bound from <c>SendGrid:EventWebhook</c>. Keep <see cref="PublicKey"/> in user-secrets
/// (Key Vault later); it is required when <c>Email:Provider</c> is SendGrid.
/// </summary>
public class EventWebhookOptions
{
    public const string SectionName = "SendGrid:EventWebhook";
    public const int DefaultMaxAgeHours = 26;

    /// <summary>The "Verification Key" from SendGrid → Mail Settings → Event Webhook (base64 SubjectPublicKeyInfo, ECDSA P-256).</summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// Requests whose signed timestamp is older than this are rejected. Deliberately generous: SendGrid retries failed posts for
    /// up to 24 h with the original signature, and replays are neutralised by <c>sg_event_id</c> idempotency, not by the clock.
    /// </summary>
    public int MaxAgeHours { get; set; } = DefaultMaxAgeHours;
}
