using System.Net;

namespace ZenLead.Domain.Templates;

/// <summary>The unsubscribe footer required on every campaign email (deliverability/compliance). Appended once, never twice.</summary>
public static class UnsubscribeFooter
{
    public const string Prompt = "Don't want to hear from us? Unsubscribe:";

    /// <summary>Stands in for the real link in previews; F23 substitutes the signed per-lead URL when sending.</summary>
    public const string PreviewUrl = "{{unsubscribeUrl}}";

    public static string Text(string url) => $"\n\n—\n{Prompt} {url}";

    public static string Html(string url)
        => $"<p style=\"margin-top:24px;font-size:12px;color:#666\">—<br>Don't want to hear from us? <a href=\"{WebUtility.HtmlEncode(url)}\">Unsubscribe</a></p>";

    public static string Append(string body, string url)
    {
        var footer = Text(url);
        return body.EndsWith(footer, StringComparison.Ordinal) ? body : body + footer;
    }

    public static string AppendHtml(string html, string url)
    {
        var footer = Html(url);
        return html.EndsWith(footer, StringComparison.Ordinal) ? html : html + footer;
    }
}
