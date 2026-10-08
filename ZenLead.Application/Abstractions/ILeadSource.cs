using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

/// <summary>All lists optional. Empty = "don't filter on this".</summary>
public record LeadSearchCriteria(
    IReadOnlyList<string> JobTitles, IReadOnlyList<string> Industries, IReadOnlyList<string> Countries,
    int? CompanySizeMin, int? CompanySizeMax, IReadOnlyList<string> CompanyDomains)
{
    public static LeadSearchCriteria Empty { get; } = new([], [], [], null, null, []);
    public bool IsEmpty => JobTitles.Count + Industries.Count + Countries.Count + CompanyDomains.Count == 0
                           && CompanySizeMin is null && CompanySizeMax is null;
}

public record DiscoveredLead(
    string ProviderId, string? FirstName, string? LastName, string? Email, string? Title,
    string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize,
    EmailVerificationStatus Verification)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public record LeadSearchPage(IReadOnlyList<DiscoveredLead> Leads, string? NextCursor, int CreditsUsed);

public enum LeadSourceFailureKind { RateLimited, Unavailable, Unauthorized, OutOfCredits, InvalidResponse }

public class LeadSourceException(LeadSourceFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public LeadSourceFailureKind Kind { get; } = kind;
    public bool IsTransient => Kind is LeadSourceFailureKind.RateLimited or LeadSourceFailureKind.Unavailable;
}

public interface ILeadSource
{
    string Name { get; }                                   // "Fake" | "Apollo" | "Pdl" — stored on the run
    Task<LeadSearchPage> SearchAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct);
    Task<int?> GetRemainingCreditsAsync(CancellationToken ct);   // null when the provider can't say
}
