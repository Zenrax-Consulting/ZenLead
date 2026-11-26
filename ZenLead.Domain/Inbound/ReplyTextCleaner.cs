using System.Text.RegularExpressions;

namespace ZenLead.Domain.Inbound;

/// <summary>Best-effort removal of quoted history from a plain-text reply. The full text is always stored next to the result.</summary>
public static class ReplyTextCleaner
{
    // Patterns are anchored per line and use [^\r\n] instead of '.', so matching stays linear; the timeout is a backstop.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Ml = RegexOptions.Multiline | RegexOptions.CultureInvariant;

    private static readonly Regex[] Cutoffs =
    [
        new(@"^[ \t]*On [^\r\n]{5,200}wrote:[ \t]*\r?$", Ml | RegexOptions.IgnoreCase, Timeout),          // Gmail/Apple: "On Tue, 5 Oct 2026 at 10:15, X <x@y> wrote:"
        new(@"^[ \t]*-{2,}[ \t]*Original Message[ \t]*-{2,}", Ml | RegexOptions.IgnoreCase, Timeout),     // Outlook
        new(@"^[ \t]*From:[ \t][^\r\n]+\r?\n[ \t]*Sent:[ \t]", Ml | RegexOptions.IgnoreCase, Timeout),     // Outlook header block
        new(@"^[ \t]*_{5,}[ \t]*\r?$", Ml, Timeout),
        new(@"^[ \t]*Le [^\r\n]{5,200}a écrit[ \t]*:[ \t]*\r?$", Ml | RegexOptions.IgnoreCase, Timeout)   // FR; add locales when seen in real mail
    ];

    public static string StripQuoted(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var cut = text.Length;
        foreach (var regex in Cutoffs)
        {
            try
            {
                var m = regex.Match(text);
                if (m.Success && m.Index < cut) cut = m.Index;
            }
            catch (RegexMatchTimeoutException) { /* heuristic only: skip this rule */ }
        }
        var head = text[..cut];
        var lines = head.Split('\n').Where(l => !l.TrimStart().StartsWith('>'));                      // drop inline-quoted lines
        var cleaned = string.Join('\n', lines).Trim();
        return cleaned.Length == 0 ? text.Trim() : cleaned;                                            // never return an empty reply because the heuristic over-matched
    }
}
