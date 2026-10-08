using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Discovery;

public record LeadProfileInput(string? Title, string? Industry, string? Country, string? CompanySize, LeadStatus Status);

public record SuggestedProfile(string Name, IReadOnlyList<string> JobTitles, IReadOnlyList<string> Industries,
                               IReadOnlyList<string> Countries, int? SizeMin, int? SizeMax);

public static class TargetProfileSuggester
{
    private const int TopN = 3;

    public static SuggestedProfile Suggest(IReadOnlyList<LeadProfileInput> leads, string? singleLeadName = null, bool onlyReplied = false)
    {
        var source = onlyReplied ? leads.Where(l => l.Status == LeadStatus.Replied).ToList() : leads.ToList();
        if (source.Count == 0) throw new InvalidOperationException("No leads to build a profile from.");

        var sizes = source.Select(l => CompanySizeParser.TryParse(l.CompanySize)).Where(r => r is not null).Select(r => r!.Value).ToList();
        return new SuggestedProfile(
            Name: source.Count == 1 && singleLeadName is not null ? $"Similar to {singleLeadName}" : $"Profile from {source.Count} leads",
            JobTitles: Top(source.Select(l => l.Title)),
            Industries: Top(source.Select(l => l.Industry)),
            Countries: Top(source.Select(l => l.Country)),
            SizeMin: sizes.Count == 0 ? null : sizes.Min(s => s.Min),
            SizeMax: sizes.Count == 0 || sizes.Any(s => s.Max is null) ? null : sizes.Max(s => s.Max));   // an open-ended size ("1000+") leaves the range open
    }

    /// <summary>Most frequent non-blank values (case-insensitive), most frequent first, ties by first appearance.</summary>
    private static IReadOnlyList<string> Top(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
              .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
              .OrderByDescending(g => g.Count()).Take(TopN).Select(g => g.First()).ToList();
}
