using ZenLead.Application.Dtos.Leads;

namespace ZenLead.Application.Abstractions;

public interface ICompanyRepository
{
    Task<IReadOnlyList<CompanySummary>> SearchAsync(Guid workspaceId, string? q, int max, CancellationToken ct = default);
    Task<CompanySummary?> GetByIdAsync(Guid workspaceId, Guid id, CancellationToken ct = default);
}
