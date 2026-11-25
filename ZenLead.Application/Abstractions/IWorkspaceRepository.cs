using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface IWorkspaceRepository
{
    Task<Workspace> CreateAsync(string name, CancellationToken ct = default);
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Sets the workspace time zone (IANA id, validated by the caller). Returns false if the workspace does not exist.</summary>
    Task<bool> UpdateTimeZoneAsync(Guid id, string timeZone, CancellationToken ct = default);
}
