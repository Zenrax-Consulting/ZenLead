using ZenLead.Domain.Entities;

namespace ZenLead.Application.Abstractions;

public interface ICsvImportRepository
{
    Task AddAsync(CsvImportBatch batch, CancellationToken ct = default);
    Task<CsvImportBatch?> GetAsync(Guid id, CancellationToken ct = default);          // HTTP requests: ambient workspace filter
    /// <summary>For Hangfire: no HTTP context, so the workspace is scoped explicitly.</summary>
    Task<CsvImportBatch?> GetForJobAsync(Guid id, Guid workspaceId, CancellationToken ct = default);
    Task UpdateAsync(CsvImportBatch batch, CancellationToken ct = default);
    /// <summary>Flips a non-completed batch to Failed so the UI never spins forever after an unexpected error.</summary>
    Task MarkFailedAsync(Guid id, Guid workspaceId, string reason, CancellationToken ct = default);
}
