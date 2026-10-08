using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface IWorkspaceRepository
{
    Task<Workspace> CreateAsync(string name, CancellationToken ct = default);
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
