using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Infrastructure.Persistence;

public class CsvImportRepository(ZenLeadDbContext db) : ICsvImportRepository
{
    public async Task AddAsync(CsvImportBatch batch, CancellationToken ct = default)
    {
        db.CsvImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);
    }

    public Task<CsvImportBatch?> GetAsync(Guid id, CancellationToken ct = default)
        => db.CsvImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);

    public Task<CsvImportBatch?> GetForJobAsync(Guid id, Guid workspaceId, CancellationToken ct = default)
        => db.CsvImportBatches.ForWorkspace(workspaceId).FirstOrDefaultAsync(b => b.Id == id, ct);

    public async Task UpdateAsync(CsvImportBatch batch, CancellationToken ct = default)
    {
        // lead ingestion shares this DbContext and may call ChangeTracker.Clear() on a concurrent-insert fallback, which detaches the batch
        if (db.Entry(batch).State == EntityState.Detached) db.CsvImportBatches.Update(batch);
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid id, Guid workspaceId, string reason, CancellationToken ct = default)
    {
        var finishedAt = DateTime.UtcNow;
        await db.CsvImportBatches.ForWorkspace(workspaceId)
            .Where(b => b.Id == id && b.Status != CsvImportStatus.Completed)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, CsvImportStatus.Failed)
                                      .SetProperty(b => b.FailureReason, reason)
                                      .SetProperty(b => b.FinishedAt, finishedAt), ct);
    }
}
