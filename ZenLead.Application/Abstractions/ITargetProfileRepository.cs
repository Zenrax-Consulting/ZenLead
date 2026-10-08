using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface ITargetProfileRepository
{
    Task<TargetProfile?> GetAsync(Guid id, CancellationToken ct = default);              // scoped by the ambient workspace filter
    Task<IReadOnlyList<TargetProfile>> ListAsync(CancellationToken ct = default);
    Task<TargetProfile> AddAsync(TargetProfile profile, CancellationToken ct = default);
    Task UpdateAsync(TargetProfile profile, CancellationToken ct = default);
    Task DeleteAsync(TargetProfile profile, CancellationToken ct = default);
}
