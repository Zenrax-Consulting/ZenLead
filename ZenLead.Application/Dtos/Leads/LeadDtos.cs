using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record CreateLeadRequest(string Name, string Email, string? Title, string? CompanyName = null, string? CompanyDomain = null);
public record UpdateLeadRequest(string Name, string? Title, LeadStatus Status, string? CompanyName, string? CompanyDomain);

public record CompanySummary(Guid Id, string Name, string? Domain, string? Industry, string? Country);

public record LeadResponse(
    Guid Id, string Name, string Email, string? Title, LeadStatus Status, DateTime CreatedAt,
    CompanySummary? Company, LeadSource Source, Guid? SourceRunId, EmailVerificationStatus EmailVerificationStatus);
