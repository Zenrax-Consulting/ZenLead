using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Discovery;

public record CriteriaDto(IReadOnlyList<string> JobTitles, IReadOnlyList<string> Industries, IReadOnlyList<string> Countries,
                          int? CompanySizeMin, int? CompanySizeMax, IReadOnlyList<string> CompanyDomains);
public record TargetProfileRequest(string Name, CriteriaDto Criteria);
public record TargetProfileResponse(Guid Id, string Name, CriteriaDto Criteria, Guid? SourceLeadId, DateTime CreatedAt);
public record SuggestFromLeadsRequest(IReadOnlyList<Guid> LeadIds, bool OnlyReplied = false);
public record SuggestedProfileResponse(string Name, CriteriaDto Criteria, Guid? SourceLeadId);
public record StartRunRequest(int MaxLeads);
public record DiscoveryRunResponse(Guid Id, Guid? TargetProfileId, string Provider, DiscoveryRunStatus Status, int RequestedCount,
    int FoundCount, int ImportedCount, int SkippedDuplicateCount, int SkippedSuppressedCount, int NoEmailCount, int CreditsUsed,
    string? FailureReason, DateTime CreatedAt, DateTime? FinishedAt)
{
    public static DiscoveryRunResponse From(LeadDiscoveryRun r) => new(r.Id, r.TargetProfileId, r.Provider, r.Status, r.RequestedCount,
        r.FoundCount, r.ImportedCount, r.SkippedDuplicateCount, r.SkippedSuppressedCount, r.NoEmailCount, r.CreditsUsed,
        r.FailureReason, r.CreatedAt, r.FinishedAt);
}
public record CreditsResponse(int Used, int Cap, int Remaining, int? ProviderRemaining, int MaxLeadsPerRun);
