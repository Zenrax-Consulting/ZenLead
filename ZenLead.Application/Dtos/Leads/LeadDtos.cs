using ZenLead.Domain.Enums;

namespace ZenLead.Application.Dtos.Leads;

public record CreateLeadRequest(string Name, string Email, string? Title);
public record LeadResponse(Guid Id, string Name, string Email, string? Title, LeadStatus Status, DateTime CreatedAt);
