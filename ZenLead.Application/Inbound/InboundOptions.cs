namespace ZenLead.Application.Inbound;

public enum InboundParseMode
{
    /// <summary>SendGrid's default: parsed multipart fields (from, to, subject, text, html, headers, ...).</summary>
    Fields,

    /// <summary>"POST the raw, full MIME message" ticked on the Inbound Parse host: the <c>email</c> field is parsed with MimeKit.</summary>
    RawMime
}

/// <summary>Bound from the "Inbound" configuration section.</summary>
public class InboundOptions
{
    public const string SectionName = "Inbound";

    public InboundParseMode ParseMode { get; set; } = InboundParseMode.Fields;

    /// <summary>Level 3 (sender-email) matching only considers leads we emailed within this many days.</summary>
    public int SenderMatchDays { get; set; } = 60;
}
