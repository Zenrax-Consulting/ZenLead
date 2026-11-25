using Hangfire;
using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Leads.Import;

namespace ZenLead.Infrastructure.Jobs;

public class ProcessCsvImportJob(ProcessCsvImportUseCase useCase, ICsvImportRepository batches, ILogger<ProcessCsvImportJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 900)]
    public async Task ExecuteAsync(Guid batchId, Guid workspaceId, CancellationToken ct)
    {
        try { await useCase.ExecuteAsync(batchId, workspaceId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "CSV import {BatchId} failed", batchId);
            await batches.MarkFailedAsync(batchId, workspaceId, "Unexpected error while importing. Retrying...", CancellationToken.None);
            throw;                                                  // Hangfire retries; the checkpoint makes the retry resume, not restart
        }
    }
}
