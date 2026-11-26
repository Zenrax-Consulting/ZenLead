using SendGrid.Helpers.Mail;
using ZenLead.Application.Email;
using OutboundEmail = ZenLead.Application.Abstractions.OutboundEmail;

namespace ZenLead.Infrastructure.Email;

/// <summary>Maps our provider-neutral <see cref="OutboundEmail"/> to a SendGrid v3 message. Pure, so it is the unit-tested part.</summary>
public static class SendGridPayloadMapper
{
    // headers SendGrid refuses (or that we set through dedicated fields); compared case-insensitively
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-sg-id", "x-sg-eid", "received", "dkim-signature", "Content-Type", "Content-Transfer-Encoding",
        "To", "From", "Subject", "Reply-To", "CC", "BCC"
    };

    public const int MaxCustomArgsBytes = 10_000; // SendGrid limit on the total size of custom args

    public static SendGridMessage ToMessage(OutboundEmail e, EmailOptions o)
    {
        var from = e.From ?? new ZenLead.Application.Abstractions.EmailAddress(o.FromAddress, o.FromName);
        var msg = new SendGridMessage
        {
            From = new EmailAddress(from.Address, from.Name),
            Subject = e.Subject,
            PlainTextContent = e.TextBody,
            HtmlContent = e.HtmlBody
        };
        msg.AddTo(e.To.Address, e.To.Name);
        if (e.ReplyTo is not null) msg.SetReplyTo(new EmailAddress(e.ReplyTo.Address, e.ReplyTo.Name));

        if (e.Headers is not null)
            foreach (var (k, v) in e.Headers)
            {
                EnsureNoLineBreaks(k, v, "header");
                if (ReservedHeaders.Contains(k))
                    throw new ArgumentException($"'{k}' is a reserved header and cannot be set directly.", nameof(e));
                msg.AddHeader(k, v);
            }

        if (e.CustomArgs is not null)
        {
            var size = 0;
            foreach (var (k, v) in e.CustomArgs)
            {
                EnsureNoLineBreaks(k, v, "custom arg");
                size += System.Text.Encoding.UTF8.GetByteCount(k) + System.Text.Encoding.UTF8.GetByteCount(v);
                msg.AddCustomArg(k, v);
            }
            if (size > MaxCustomArgsBytes)
                throw new ArgumentException($"Custom args exceed SendGrid's {MaxCustomArgsBytes}-byte limit.", nameof(e));
        }

        if (e.Categories is { Count: > 0 }) msg.AddCategories(e.Categories.ToList());
        msg.SetClickTracking(false, false);
        msg.SetOpenTracking(e.TrackOpens);
        msg.SetSubscriptionTracking(false);
        return msg;
    }

    // header injection guard: lead-derived strings (F23) must never smuggle extra headers
    private static void EnsureNoLineBreaks(string key, string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(key) || key.AsSpan().IndexOfAny('\r', '\n') >= 0 || value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new ArgumentException($"Email {kind} '{Sanitize(key)}' is empty or contains a line break.");
    }

    private static string Sanitize(string s) => s.Replace("\r", "\r").Replace("\n", "\n");
}
