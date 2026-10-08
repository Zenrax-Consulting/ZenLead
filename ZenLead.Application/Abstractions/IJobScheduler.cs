namespace ZenLead.Application.Abstractions;

/// <summary>Application never sees Hangfire. Each job feature adds one method.</summary>
public interface IJobScheduler
{
    void EnqueueDiscoveryRun(Guid runId, Guid workspaceId);
    void EnqueueCsvImport(Guid batchId, Guid workspaceId);
    // F23 registers the recurring sender in Infrastructure; F26 adds EnqueueReplyClassification.
}
