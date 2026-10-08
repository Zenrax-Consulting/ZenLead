using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface IDiscoveryRunRepository
{
    Task AddAsync(LeadDiscoveryRun run, CancellationToken ct = default);
    Task<LeadDiscoveryRun?> GetAsync(Guid id, CancellationToken ct = default);          // HTTP requests: ambient workspace filter
    /// <summary>For Hangfire: no HTTP context, so the workspace is scoped explicitly.</summary>
    Task<LeadDiscoveryRun?> GetForJobAsync(Guid id, Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<LeadDiscoveryRun>> ListAsync(Guid workspaceId, Guid? targetProfileId, int take, CancellationToken ct = default);
    Task<bool> HasActiveRunAsync(Guid workspaceId, CancellationToken ct = default);     // Queued or Running
    Task<int> GetCreditsUsedThisMonthAsync(Guid workspaceId, DateTime monthStartUtc, CancellationToken ct = default);
    Task UpdateAsync(LeadDiscoveryRun run, CancellationToken ct = default);
}
