using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

public class DiscoveryRunRepository(ZenLeadDbContext db) : IDiscoveryRunRepository
{
    public async Task AddAsync(LeadDiscoveryRun run, CancellationToken ct = default)
    {
        db.DiscoveryRuns.Add(run);
        await db.SaveChangesAsync(ct);
    }

    public Task<LeadDiscoveryRun?> GetAsync(Guid id, CancellationToken ct = default)
        => db.DiscoveryRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<LeadDiscoveryRun?> GetForJobAsync(Guid id, Guid workspaceId, CancellationToken ct = default)
        => db.DiscoveryRuns.ForWorkspace(workspaceId).FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<LeadDiscoveryRun>> ListAsync(Guid workspaceId, Guid? targetProfileId, int take, CancellationToken ct = default)
    {
        var query = db.DiscoveryRuns.AsNoTracking().ForWorkspace(workspaceId);
        if (targetProfileId is { } pid) query = query.Where(r => r.TargetProfileId == pid);
        return await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Take(take).ToListAsync(ct);
    }

    public Task<bool> HasActiveRunAsync(Guid workspaceId, CancellationToken ct = default)
        => db.DiscoveryRuns.ForWorkspace(workspaceId)
            .AnyAsync(r => r.Status == DiscoveryRunStatus.Queued || r.Status == DiscoveryRunStatus.Running, ct);

    public async Task<int> GetCreditsUsedThisMonthAsync(Guid workspaceId, DateTime monthStartUtc, CancellationToken ct = default)
        => await db.DiscoveryRuns.ForWorkspace(workspaceId)
            .Where(r => r.CreatedAt >= monthStartUtc)
            .SumAsync(r => (int?)r.CreditsUsed, ct) ?? 0;

    public async Task UpdateAsync(LeadDiscoveryRun run, CancellationToken ct = default)
    {
        // lead ingestion shares this DbContext and may call ChangeTracker.Clear() on a concurrent-insert fallback, which detaches the run
        if (db.Entry(run).State == EntityState.Detached) db.DiscoveryRuns.Update(run);
        await db.SaveChangesAsync(ct);
    }
}
