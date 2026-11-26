namespace ZenLead.Application.Abstractions;

/// <summary>An inbound email as the use case sees it, independent of how SendGrid posted it (parsed fields or raw MIME).</summary>
/// <param name="ToAddresses">Visible <c>To</c> and the SMTP envelope recipients, merged and deduped (the envelope carries the reply token).</param>
/// <param name="MessageId">Without the angle brackets.</param>
/// <param name="Headers">Case-insensitive, unfolded. Repeated headers keep the first value.</param>
/// <param name="Dkim">SendGrid's verdict, e.g. <c>{@gmail.com : pass}</c>.</param>
/// <param name="Spf">SendGrid's verdict, e.g. <c>pass</c>.</param>
public record InboundEmail(
    string FromAddress, string? FromName, IReadOnlyList<string> ToAddresses, string Subject,
    string? Text, string? Html, string? MessageId, string? InReplyTo, IReadOnlyList<string> References,
    IReadOnlyDictionary<string, string> Headers, string? Dkim, string? Spf, DateTime ReceivedAt);

/// <summary>
/// Turns SendGrid's Inbound Parse post into an <see cref="InboundEmail"/>. Application doesn't reference ASP.NET, so the controller
/// flattens the form's <em>text fields</em> into a dictionary; attachments are never passed in. Chosen by <c>Inbound:ParseMode</c>.
/// </summary>
public interface IInboundEmailParser
{
    /// <summary>Throws <see cref="InboundParseException"/> for input that is not an inbound-parse post at all.</summary>
    InboundEmail Parse(IReadOnlyDictionary<string, string> fields, DateTime receivedAtUtc);
}

public class InboundParseException(string message) : Exception(message);

/// <summary>Makes untrusted HTML safe to store (and, one day, to render). F27 renders text only.</summary>
public interface IHtmlSanitizer
{
    string Sanitize(string html);
}
