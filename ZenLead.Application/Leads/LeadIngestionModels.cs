using ZenLead.Domain.Enums;

namespace ZenLead.Application.Leads;

public record CandidateLead(
    string? Name, string? Email, string? Title,
    string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize = null,
    EmailVerificationStatus Verification = EmailVerificationStatus.Unverified);

public record IngestionSource(LeadSource Source, Guid? SourceRunId);

public enum IngestionOutcome { Imported, Duplicate, Suppressed, Invalid }

/// <param name="Index">Position of the row in the input list, so callers can map back to CSV row numbers.</param>
public record IngestionRowResult(int Index, IngestionOutcome Outcome, string? Reason = null, Guid? LeadId = null);

public record IngestionSummary(IReadOnlyList<IngestionRowResult> Rows)
{
    public int Imported => Rows.Count(r => r.Outcome == IngestionOutcome.Imported);
    public int Duplicates => Rows.Count(r => r.Outcome == IngestionOutcome.Duplicate);
    public int Suppressed => Rows.Count(r => r.Outcome == IngestionOutcome.Suppressed);
    public int Invalid => Rows.Count(r => r.Outcome == IngestionOutcome.Invalid);
}
