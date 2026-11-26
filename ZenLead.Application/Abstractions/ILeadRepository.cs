using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface ILeadRepository
{
    Task<Lead> CreateAsync(Lead lead, CancellationToken ct = default);
    Task<Lead?> GetByIdAsync(Guid id, CancellationToken ct = default);              // includes Company, excludes soft-deleted (query filter)
    Task<PagedResult<Lead>> SearchAsync(Guid workspaceId, LeadQuery query, CancellationToken ct = default);
    /// <summary>Ids of every lead matching the filter (paging ignored), capped at <paramref name="max"/>.</summary>
    Task<IReadOnlyList<Guid>> ListIdsAsync(Guid workspaceId, LeadQuery query, int max, CancellationToken ct = default);
    /// <summary>Leads of this workspace among <paramref name="ids"/> (includes Company). Ids from other workspaces simply don't resolve.</summary>
    Task<IReadOnlyList<Lead>> GetByIdsAsync(Guid workspaceId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>
    /// For code with no current workspace (jobs, the public unsubscribe endpoint): tracked, explicitly scoped to
    /// <paramref name="workspaceId"/> (never one taken from request input), soft-deleted leads included.
    /// </summary>
    Task<Lead?> GetForJobAsync(Guid workspaceId, Guid leadId, CancellationToken ct = default);
    Task UpdateAsync(Lead lead, CancellationToken ct = default);
    Task SoftDeleteAsync(Lead lead, CancellationToken ct = default);
}
