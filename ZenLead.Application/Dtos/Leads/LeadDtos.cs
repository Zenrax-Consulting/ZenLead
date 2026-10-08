using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record CreateLeadRequest(string Name, string Email, string? Title, string? CompanyName = null, string? CompanyDomain = null);
public record UpdateLeadRequest(string Name, string? Title, LeadStatus Status, string? CompanyName, string? CompanyDomain);

public record LeadQuery(
    int Page = 1, int PageSize = 25, string? Q = null,
    LeadStatus? Status = null, Guid? CompanyId = null, LeadSource? Source = null, Guid? SourceRunId = null,
    string? Sort = null);   // "name" | "email" | "status" | "company" | "createdAt"; prefix "-" = descending; default "-createdAt"

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public record CompanySummary(Guid Id, string Name, string? Domain, string? Industry, string? Country);

public record LeadResponse(
    Guid Id, string Name, string Email, string? Title, LeadStatus Status, DateTime CreatedAt,
    CompanySummary? Company, LeadSource Source, Guid? SourceRunId, EmailVerificationStatus EmailVerificationStatus);
