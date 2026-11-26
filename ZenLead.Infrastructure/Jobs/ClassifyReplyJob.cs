using Hangfire;
using ZenLead.Application.UseCases.Classification;

namespace ZenLead.Infrastructure.Jobs;

/// <summary>
/// Classifies one inbound reply (F26), enqueued by the inbound webhook after the reply is committed. Hangfire's global retry filter
/// covers transient provider errors; after the last attempt the message simply stays Unclassified.
/// </summary>
public class ClassifyReplyJob(ClassifyReplyUseCase useCase)
{
    public const string LockResource = "classify-reply:{0}:{1}";

    [DisableConcurrentExecution(LockResource, 120)]         // keyed by the (workspaceId, messageId) arguments
    public Task ExecuteAsync(Guid workspaceId, Guid messageId, CancellationToken ct) => useCase.ExecuteAsync(workspaceId, messageId, ct);
}
