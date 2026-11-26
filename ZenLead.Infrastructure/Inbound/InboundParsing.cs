using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZenLead.Infrastructure.Inbound;

/// <summary>Tolerant helpers shared by both inbound parsers. Nothing here throws on odd input.</summary>
internal static partial class InboundParsing
{
    [GeneratedRegex(@"[^\s<>""',;:()\[\]]+@[^\s<>""',;:()\[\]]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex AddressPattern();

    /// <summary><c>"Name" &lt;addr&gt;</c>, <c>Name &lt;addr&gt;</c> or a bare address. Falls back to the first address-looking token.</summary>
    public static (string? Address, string? Name) ParseMailbox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        try
        {
            var parsed = new MailAddress(value.Trim());
            return (parsed.Address, string.IsNullOrWhiteSpace(parsed.DisplayName) ? null : parsed.DisplayName.Trim());
        }
        catch (FormatException) { }

        var addresses = Addresses(value);
        if (addresses.Count == 0) return (null, null);
        var address = addresses[^1];                                     // the one in <> comes last in "Name, with comma <a@b>"
        var lt = value.LastIndexOf('<');
        var name = lt > 0 ? value[..lt].Trim().Trim('"').Trim() : null;
        return (address, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    /// <summary>Every address in a header-ish list (names with commas or quotes are fine).</summary>
    public static List<string> Addresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            return AddressPattern().Matches(value).Select(m => m.Value.Trim('.')).Where(a => a.Length > 2).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary>SendGrid's <c>envelope</c> field: <c>{"to":["r-…@reply…"],"from":"jane@example.com"}</c>.</summary>
    public static List<string> EnvelopeRecipients(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(envelopeJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("to", out var to)) return [];
            return to.ValueKind switch
            {
                JsonValueKind.Array => to.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).SelectMany(e => Addresses(e.GetString())).ToList(),
                JsonValueKind.String => Addresses(to.GetString()),
                _ => []
            };
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Envelope recipients first (they reached our MX and carry the token), then the visible To; deduped case-insensitively.</summary>
    public static List<string> MergeRecipients(IEnumerable<string> envelope, IEnumerable<string> visible)
        => envelope.Concat(visible).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>RFC 5322 header block → case-insensitive dictionary; continuation lines unfolded; the first of repeated headers wins.</summary>
    public static Dictionary<string, string> ParseHeaders(string? block)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(block)) return headers;
        string? name = null;
        var value = new StringBuilder();
        void Flush()
        {
            if (name is not null && !headers.ContainsKey(name)) headers[name] = value.ToString().Trim();
            name = null;
            value.Clear();
        }
        foreach (var rawLine in block.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length == 0) { Flush(); break; }                   // end of the header block
            if (rawLine[0] is ' ' or '\t')
            {
                if (name is not null) value.Append(' ').Append(rawLine.Trim());
                continue;
            }
            Flush();
            var colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;                                       // not a header line: ignore
            name = rawLine[..colon].Trim();
            value.Append(rawLine[(colon + 1)..].Trim());
        }
        Flush();
        return headers;
    }

    /// <summary><c>&lt;a@b&gt; &lt;c@d&gt;</c> → <c>["a@b", "c@d"]</c>.</summary>
    public static List<string> MessageIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(StripBrackets).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    public static string StripBrackets(string id) => id.Trim().Trim('<', '>').Trim();

    public static string? Field(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (fields.TryGetValue(key, out var v)) return string.IsNullOrEmpty(v) ? null : v;
        foreach (var (k, value) in fields)
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return string.IsNullOrEmpty(value) ? null : value;
        return null;
    }
}
