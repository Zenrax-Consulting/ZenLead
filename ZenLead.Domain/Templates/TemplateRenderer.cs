using System.Text.RegularExpressions;
using ZenLead.Domain.Entities;

namespace ZenLead.Domain.Templates;

public record RenderedTemplate(string Text, IReadOnlyList<string> MissingTokens);

/// <summary>
/// Merge tokens in campaign templates: <c>{{firstName}}</c>, <c>{{lastName}}</c>, <c>{{name}}</c>, <c>{{company}}</c>, <c>{{title}}</c>,
/// optionally with a fallback (<c>{{firstName|there}}</c>). Names are case-insensitive; whitespace inside the braces is ignored.
/// </summary>
public static class TemplateRenderer
{
    public static readonly IReadOnlySet<string> KnownTokens =
        new HashSet<string>(["firstName", "lastName", "name", "company", "title"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TokenPattern = new(@"\{\{\s*(?<name>[A-Za-z]+)\s*(\|(?<fallback>[^}]*))?\}\}", RegexOptions.Compiled);
    private static readonly Regex ControlChars = new(@"[\u0000-\u001F\u007F]+", RegexOptions.Compiled);

    public static IReadOnlyList<string> UnknownTokens(string template)
        => TokenPattern.Matches(template).Select(m => m.Groups["name"].Value).Where(n => !KnownTokens.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Tokens with no fallback: a lead without a value for one of these can't be sent this template.</summary>
    public static IReadOnlyList<string> RequiredTokens(string template)
        => TokenPattern.Matches(template).Where(m => !m.Groups["fallback"].Success).Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static RenderedTemplate Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var missing = new List<string>();
        var text = TokenPattern.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            values.TryGetValue(name, out var value);
            if (!string.IsNullOrWhiteSpace(value)) return Sanitize(value);
            if (m.Groups["fallback"].Success) return Sanitize(m.Groups["fallback"].Value.Trim());
            if (!missing.Contains(name, StringComparer.OrdinalIgnoreCase)) missing.Add(name);
            return string.Empty;
        });
        return new RenderedTemplate(text, missing);
    }

    /// <summary>Lead data is untrusted (CSV/provider). No CR/LF/control chars → no header injection through subjects, no layout games in bodies.</summary>
    private static string Sanitize(string v) => ControlChars.Replace(v.Trim(), " ");

    /// <summary>Token values for a lead; first/last name split on the first space.</summary>
    public static Dictionary<string, string?> ValuesFor(Lead lead)
    {
        var parts = (lead.Name ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["firstName"] = parts.ElementAtOrDefault(0),
            ["lastName"] = parts.ElementAtOrDefault(1),
            ["name"] = lead.Name,
            ["company"] = lead.Company?.Name,
            ["title"] = lead.Title
        };
    }
}
