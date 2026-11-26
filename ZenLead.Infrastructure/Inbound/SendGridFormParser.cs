using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using static ZenLead.Infrastructure.Inbound.InboundParsing;

namespace ZenLead.Infrastructure.Inbound;

/// <summary>
/// SendGrid Inbound Parse, default mode (parsed multipart fields): <c>from</c>, <c>to</c>, <c>subject</c>, <c>text</c>, <c>html</c>,
/// <c>headers</c>, <c>dkim</c>, <c>SPF</c>, <c>envelope</c>, <c>charsets</c>. ASP.NET has already decoded the fields as UTF-8, so a
/// sender using another charset can arrive garbled; that is logged as the signal to switch to <see cref="MimeKitInboundParser"/>.
/// </summary>
public class SendGridFormParser(ILogger<SendGridFormParser> logger) : IInboundEmailParser
{
    public InboundEmail Parse(IReadOnlyDictionary<string, string> fields, DateTime receivedAtUtc)
    {
        var (fromAddress, fromName) = ParseMailbox(Field(fields, "from"));
        if (fromAddress is null) throw new InboundParseException("The post has no usable 'from' field.");

        var recipients = MergeRecipients(EnvelopeRecipients(Field(fields, "envelope")), Addresses(Field(fields, "to")));
        if (recipients.Count == 0) throw new InboundParseException("The post has no recipients ('to' / 'envelope').");

        var headers = ParseHeaders(Field(fields, "headers"));
        WarnOnNonUtf8(Field(fields, "charsets"));

        return new InboundEmail(
            FromAddress: fromAddress,
            FromName: fromName,
            ToAddresses: recipients,
            Subject: Field(fields, "subject") ?? (headers.TryGetValue("Subject", out var s) ? s : ""),
            Text: Field(fields, "text"),
            Html: Field(fields, "html"),
            MessageId: headers.TryGetValue("Message-ID", out var id) ? MessageIds(id).FirstOrDefault() : null,
            InReplyTo: headers.TryGetValue("In-Reply-To", out var irt) ? MessageIds(irt).FirstOrDefault() : null,
            References: headers.TryGetValue("References", out var refs) ? MessageIds(refs) : [],
            Headers: headers,
            Dkim: Field(fields, "dkim"),
            Spf: Field(fields, "SPF"),
            ReceivedAt: receivedAtUtc);
    }

    /// <summary><c>charsets</c> is JSON like <c>{"to":"UTF-8","text":"iso-8859-1",...}</c>.</summary>
    private void WarnOnNonUtf8(string? charsetsJson)
    {
        if (string.IsNullOrWhiteSpace(charsetsJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(charsetsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var field in new[] { "text", "html", "subject", "from" })
                if (doc.RootElement.TryGetProperty(field, out var cs) && cs.ValueKind == JsonValueKind.String
                    && cs.GetString() is { Length: > 0 } charset && !IsUtf8OrAscii(charset))
                {
                    logger.LogWarning("Inbound email field {Field} uses charset {Charset}; parsed-fields mode may garble it. Consider Inbound:ParseMode=RawMime.",
                        field, charset);
                    return;                                                  // one line per email
                }
        }
        catch (JsonException) { }
    }

    private static bool IsUtf8OrAscii(string charset)
        => charset.Trim().ToLowerInvariant() is "utf-8" or "utf8" or "us-ascii" or "ascii";
}
