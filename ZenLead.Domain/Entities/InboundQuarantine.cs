namespace ZenLead.Domain.Entities;

/// <summary>
/// Inbound mail we could not (or must not) attach to a thread. Not tenant-scoped on purpose: by definition we couldn't determine
/// the workspace. Holds headers only, never the body (reply content is personal data). A 30-day purge is F29 housekeeping.
/// </summary>
public class InboundQuarantine
{
    public const string NoMatch = "no_match";
    public const string Ambiguous = "ambiguous";
    public const string SpoofSuspected = "spoof_suspected";
    public const string TooLarge = "too_large";
    public const string ParseError = "parse_error";

    public Guid Id { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Reason { get; set; } = string.Empty;          // no_match | ambiguous | spoof_suspected | too_large | parse_error
    public string? FromAddress { get; set; }
    public string? ToAddress { get; set; }
    public string? Subject { get; set; }
    public string? HeadersSnippet { get; set; }                 // first 4 KB of headers, no body (reply content is personal data)
}
