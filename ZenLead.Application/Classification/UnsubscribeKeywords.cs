using System.Text.RegularExpressions;

namespace ZenLead.Application.Classification;

/// <summary>
/// Deterministic backstop for the classifier (F26): only ever <em>raises</em> the confidence of an <c>unsubscribe</c> label the model
/// already chose, never creates one. Run it against the cleaned reply text only (not quoted history, which contains our own footer).
/// False positives ("I can't unsubscribe from my gym") are why the model must agree first.
/// </summary>
public static class UnsubscribeKeywords
{
    private static readonly string[] Phrases =
    [
        "unsubscribe", "remove me", "remove my", "take me off", "stop emailing", "stop sending",
        "do not contact", "don't contact", "dont contact", "opt out", "opt-out", "no more emails"
    ];

    private static readonly Regex Pattern = new(
        @"\b(?:" + string.Join("|", Phrases.Select(p => Regex.Escape(p).Replace(@"\ ", @"\s+"))) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static bool LooksLikeUnsubscribeRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            return Pattern.IsMatch(text.Replace('’', '\''));              // curly apostrophe: "don’t contact"
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
