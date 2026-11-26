using System.Text;
using MimeKit;
using ZenLead.Application.Abstractions;
using static ZenLead.Infrastructure.Inbound.InboundParsing;

namespace ZenLead.Infrastructure.Inbound;

/// <summary>
/// SendGrid Inbound Parse with "POST the raw, full MIME message" ticked: the <c>email</c> field holds the message and MimeKit
/// extracts everything with correct charset handling. Not enabled by default — set <c>Inbound:ParseMode=RawMime</c> and tick the
/// option on the Inbound Parse host together (see plans/phase-2-features/25-inbound-webhook.md, "Parsing mode").
/// </summary>
public class MimeKitInboundParser : IInboundEmailParser
{
    public InboundEmail Parse(IReadOnlyDictionary<string, string> fields, DateTime receivedAtUtc)
    {
        var raw = Field(fields, "email") ?? throw new InboundParseException("The post has no 'email' field (is raw MIME enabled on the Inbound Parse host?).");

        MimeMessage message;
        try
        {
            // the form value was decoded as UTF-8 by ASP.NET; encoding it back gives MimeKit the original bytes for UTF-8/7-bit mail
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            message = MimeMessage.Load(stream);
        }
        catch (FormatException ex)
        {
            throw new InboundParseException($"The raw MIME message could not be parsed: {ex.Message}");
        }

        var from = message.From.Mailboxes.FirstOrDefault() ?? message.Sender;
        if (from is null || string.IsNullOrWhiteSpace(from.Address)) throw new InboundParseException("The message has no From address.");

        var visible = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address);
        var recipients = MergeRecipients(EnvelopeRecipients(Field(fields, "envelope")), visible);
        if (recipients.Count == 0) throw new InboundParseException("The message has no recipients.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in message.Headers)
            headers.TryAdd(header.Field, header.Value.Trim());

        return new InboundEmail(
            FromAddress: from.Address,
            FromName: string.IsNullOrWhiteSpace(from.Name) ? null : from.Name.Trim(),
            ToAddresses: recipients,
            Subject: message.Subject ?? "",
            Text: message.TextBody,
            Html: message.HtmlBody,
            MessageId: string.IsNullOrWhiteSpace(message.MessageId) ? null : StripBrackets(message.MessageId),
            InReplyTo: string.IsNullOrWhiteSpace(message.InReplyTo) ? null : StripBrackets(message.InReplyTo),
            References: message.References.Select(StripBrackets).Where(r => r.Length > 0).ToList(),
            Headers: headers,
            Dkim: Field(fields, "dkim"),
            Spf: Field(fields, "SPF"),
            ReceivedAt: receivedAtUtc);
    }
}
