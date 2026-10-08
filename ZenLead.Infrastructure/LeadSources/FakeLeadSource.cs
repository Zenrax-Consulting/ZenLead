using ZenLead.Application.Abstractions;
using ZenLead.Domain.Discovery;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.LeadSources;

/// <summary>Deterministic, no network. The dev default and the test double.</summary>
public class FakeLeadSource : ILeadSource
{
    public string Name => "Fake";
    public int PageCostPerLead { get; set; } = 1;
    public Queue<Exception> FailuresToThrow { get; } = new();         // tests: enqueue failures for the next calls
    public List<(string? Cursor, int Limit)> Calls { get; } = [];

    public Task<LeadSearchPage> SearchAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct)
    {
        Calls.Add((cursor, limit));
        if (FailuresToThrow.TryDequeue(out var failure)) throw failure;

        var all = Dataset.Where(l => Matches(criteria, l)).ToList();
        var offset = cursor is null ? 0 : int.Parse(cursor);
        var page = all.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count < all.Count ? (offset + page.Count).ToString() : null;
        return Task.FromResult(new LeadSearchPage(page, next, page.Count * PageCostPerLead));
    }

    public Task<int?> GetRemainingCreditsAsync(CancellationToken ct) => Task.FromResult<int?>(null);

    private static bool Matches(LeadSearchCriteria c, DiscoveredLead l)
    {
        if (c.Countries.Count > 0 && !c.Countries.Any(x => string.Equals(x, l.Country, StringComparison.OrdinalIgnoreCase))) return false;
        if (c.JobTitles.Count > 0 && !c.JobTitles.Any(x => l.Title?.Contains(x, StringComparison.OrdinalIgnoreCase) == true)) return false;
        if (c.Industries.Count > 0 && !c.Industries.Any(x => l.Industry?.Contains(x, StringComparison.OrdinalIgnoreCase) == true)) return false;
        if (c.CompanyDomains.Count > 0 && !c.CompanyDomains.Any(x => string.Equals(x, l.CompanyDomain, StringComparison.OrdinalIgnoreCase))) return false;
        if (c.CompanySizeMin is not null || c.CompanySizeMax is not null)
        {
            var size = CompanySizeParser.TryParse(l.CompanySize);
            if (size is null) return false;
            if (c.CompanySizeMin is { } min && (size.Value.Max ?? int.MaxValue) < min) return false;
            if (c.CompanySizeMax is { } max && size.Value.Min > max) return false;
        }
        return true;
    }

    private static readonly string[] Industries = ["Software", "Finance", "Healthcare", "Retail", "Manufacturing"];
    private static readonly string[] Sizes = ["11-50", "51-200", "201-500", "1000+"];
    private static readonly string[] Countries = ["US", "GB", "DE", "IN", "CA"];
    private static readonly string[] Titles = ["VP Sales", "Head of Marketing", "CTO", "Founder", "Sales Manager", "Marketing Director"];
    private static readonly string[] First = ["Ava", "Liam", "Mia", "Noah", "Zoe", "Ethan", "Ivy", "Owen", "Ella", "Lucas"];
    private static readonly string[] Last = ["Stone", "Reed", "Khan", "Moore", "Patel", "Lopez", "Weber", "Singh", "Clark", "Young"];

    // 200 people across 20 companies. Some rows have no email, verification statuses are mixed, and a few emails repeat in different case.
    private static readonly IReadOnlyList<DiscoveredLead> Dataset = Enumerable.Range(0, 200).Select(i =>
    {
        var company = i % 20;
        var domain = $"company{company}.example.com";
        var first = First[i % First.Length];
        var last = Last[(i / First.Length + i) % Last.Length];
        string? email = i % 13 == 0 ? null : $"{first}.{last}{i}@{domain}".ToLowerInvariant();
        if (i % 29 == 0 && i > 0) email = $"{First[(i - 1) % First.Length]}.{Last[((i - 1) / First.Length + (i - 1)) % Last.Length]}{i - 1}@{domain}".ToUpperInvariant();
        var verification = (i % 7) switch
        {
            0 => EmailVerificationStatus.Invalid,
            1 or 2 => EmailVerificationStatus.Risky,
            3 => EmailVerificationStatus.Unverified,
            _ => EmailVerificationStatus.Verified
        };
        return new DiscoveredLead($"fake-{i}", first, last, email, Titles[i % Titles.Length], $"Company {company}", domain,
            Industries[company % Industries.Length], Countries[company % Countries.Length], Sizes[company % Sizes.Length], verification);
    }).ToList();
}
