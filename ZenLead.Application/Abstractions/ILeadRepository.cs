using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface ILeadRepository
{
    Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default);
    Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default);              // includes Company, excludes soft-deleted (query filter)
    Task<IReadOnlyList<Lead>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task UpdateAsync(Lead lead, CancellationToken ct = default);
    Task SoftDeleteAsync(Lead lead, CancellationToken ct = default);
}
