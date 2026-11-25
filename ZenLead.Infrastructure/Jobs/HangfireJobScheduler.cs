using Hangfire;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Jobs;

public class HangfireJobScheduler(IBackgroundJobClient client) : IJobScheduler
{
    public void EnqueueDiscoveryRun(Guid runId, Guid workspaceId)
        => client.Enqueue<ProcessLeadDiscoveryJob>(j => j.ExecuteAsync(runId, workspaceId, CancellationToken.None));   // Hangfire substitutes the shutdown token

    public void EnqueueCsvImport(Guid batchId, Guid workspaceId)
        => client.Enqueue<ProcessCsvImportJob>(j => j.ExecuteAsync(batchId, workspaceId, CancellationToken.None));
}

/// <summary>Used when Hangfire is disabled (endpoint tests boot without SQL Server): jobs are dropped.</summary>
public class NoopJobScheduler : IJobScheduler
{
    public void EnqueueDiscoveryRun(Guid runId, Guid workspaceId) { }
    public void EnqueueCsvImport(Guid batchId, Guid workspaceId) { }
}
