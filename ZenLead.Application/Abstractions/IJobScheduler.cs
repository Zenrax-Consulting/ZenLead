namespace ZenLead.Application.Abstractions;

/// <summary>Application never sees Hangfire. Each job feature adds one method.</summary>
public interface IJobScheduler
{
    void EnqueueDiscoveryRun(Guid runId, Guid workspaceId);
    // F15 adds EnqueueCsvImport; F23 registers the recurring sender in Infrastructure; F26 adds EnqueueReplyClassification.
}
