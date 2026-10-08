using Hangfire;
using ZenLead.Application.UseCases.Discovery;

namespace ZenLead.Infrastructure.Jobs;

public class ProcessLeadDiscoveryJob(ProcessDiscoveryRunUseCase useCase)
{
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task ExecuteAsync(Guid runId, Guid workspaceId, CancellationToken ct) => useCase.ExecuteAsync(runId, workspaceId, ct);
}
