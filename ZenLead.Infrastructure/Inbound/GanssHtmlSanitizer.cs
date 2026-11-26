using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Ganss.Xss;

namespace ZenLead.Infrastructure.Inbound;

/// <summary>
/// A deliberately tiny allow-list for reply HTML: basic formatting and http/https/mailto links (rel="nofollow noopener", no target).
/// No images (tracking pixels), styles, classes, forms, iframes or scripts; comments removed. Text inside removed formatting
/// containers (div, table, span…) is kept; the contents of script/style-like elements are dropped entirely.
/// </summary>
public class GanssHtmlSanitizer : ZenLead.Application.Abstractions.IHtmlSanitizer
{
    private static readonly string[] Tags = ["p", "br", "b", "strong", "i", "em", "u", "ul", "ol", "li", "blockquote", "a"];
    private static readonly HashSet<string> DropWithContents = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet", "head", "title", "noscript", "template",
        "svg", "math", "form", "select", "textarea", "button", "input", "video", "audio", "canvas", "base", "link", "meta"
    };

    private readonly HtmlSanitizer _sanitizer;

    public GanssHtmlSanitizer()
    {
        _sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["http", "https", "mailto"], StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(),
            AllowedAtRules = new HashSet<CssRuleType>(),
            AllowedClasses = new HashSet<string>()
        })
        {
            KeepChildNodes = true,
            AllowDataAttributes = false
        };
        _sanitizer.RemovingTag += (_, e) =>
        {
            if (DropWithContents.Contains(e.Tag.LocalName)) e.Tag.InnerHtml = "";     // never surface script/style text
        };
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement { LocalName: "a" } a)
            {
                a.RemoveAttribute("target");
                if (a.HasAttribute("href")) a.SetAttribute("rel", "nofollow noopener");
            }
        };
    }

    public string Sanitize(string html) => string.IsNullOrWhiteSpace(html) ? string.Empty : _sanitizer.Sanitize(html);
}
