namespace ZenLead.Api;

/// <summary>Inbound webhook settings (F25). The secret comes from <c>SendGrid:InboundSecret</c>; the capture switch from <c>Webhooks:CaptureRaw</c>.</summary>
public class WebhookOptions
{
    public const int MinInboundSecretLength = 32;

    /// <summary>Rides in the Inbound Parse URL's query string (<c>?secret=</c>). ≥ 32 random characters; required when Email:Provider=SendGrid.</summary>
    public string? InboundSecret { get; set; }

    /// <summary>Development only: write each post's text fields to <c>%TEMP%\zenlead-inbound\</c> to collect test fixtures.</summary>
    public bool CaptureRaw { get; set; }
}
