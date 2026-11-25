using System.Net;
using ZenLead.Application.Abstractions;

namespace ZenLead.Application.Email;

public static class SystemEmail
{
    /// <summary>Plain text first (always), with a minimal HTML twin. No tracking, no unsubscribe footer: these are account emails.</summary>
    public static OutboundEmail Create(string toAddress, string? toName, string subject, string text, string? actionUrl = null, string? actionLabel = null)
    {
        var html = $"<p>{WebUtility.HtmlEncode(text).Replace("\n", "<br>")}</p>"
                 + (actionUrl is null ? "" : $"<p><a href=\"{WebUtility.HtmlEncode(actionUrl)}\">{WebUtility.HtmlEncode(actionLabel ?? actionUrl)}</a></p>");
        var plain = actionUrl is null ? text : $"{text}\n\n{actionLabel ?? "Open"}: {actionUrl}";
        return new OutboundEmail(new EmailAddress(toAddress, toName), subject, plain, html, Categories: ["system"]);
    }
}
