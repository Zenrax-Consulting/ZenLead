using ZenLead.Application.Dtos.Admin;
using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface IWorkspaceRepository
{
    /// <exception cref="WorkspaceNameTakenException">Another workspace already has this name (unique index).</exception>
    Task<Workspace> CreateAsync(string name, string? adminEmail = null, string timeZone = "UTC", CancellationToken ct = default);
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Sets the workspace time zone (IANA id, validated by the caller). Returns false if the workspace does not exist.</summary>
    Task<bool> UpdateTimeZoneAsync(Guid id, string timeZone, CancellationToken ct = default);

    /// <summary>Case- and surrounding-whitespace-insensitive.</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);

    /// <summary>Platform view for the super admin: reads only Workspaces and a user count — never tenant tables.</summary>
    Task<IReadOnlyList<WorkspaceAdminResponse>> ListWithUserCountsAsync(CancellationToken ct = default);
}

public class WorkspaceNameTakenException() : InvalidOperationException("A workspace with that name already exists.");
