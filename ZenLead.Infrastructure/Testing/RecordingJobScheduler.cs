using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Jobs;

namespace ZenLead.Infrastructure.Testing;

public enum RecordedJobType { Discovery, Csv, Classify }

/// <param name="Id">The run id (discovery), batch id (csv) or message id (classify).</param>
public sealed record RecordedJob(long Sequence, RecordedJobType Type, Guid Id, Guid WorkspaceId, DateTimeOffset EnqueuedAt);

public sealed record JobRunResult(RecordedJob Job, bool Succeeded, string? Error);

/// <summary>Jobs enqueued in the E2E environment, waiting for a test to run them. Singleton.</summary>
public sealed class RecordedJobQueue
{
    private readonly ConcurrentQueue<RecordedJob> _pending = new();
    private long _sequence;

    public void Add(RecordedJobType type, Guid id, Guid workspaceId, DateTimeOffset at)
        => _pending.Enqueue(new RecordedJob(Interlocked.Increment(ref _sequence), type, id, workspaceId, at));

    public IReadOnlyList<RecordedJob> Pending => _pending.ToArray();

    public bool TryTake(out RecordedJob job) => _pending.TryDequeue(out job!);

    public void Clear() => _pending.Clear();
}

/// <summary>
/// The E2E environment's <see cref="IJobScheduler"/> (Hangfire is off there): records each enqueue instead of running it, so tests
/// decide when background work happens (<c>POST /api/v1/test/jobs/run-pending</c>).
/// </summary>
public sealed class RecordingJobScheduler(RecordedJobQueue queue, TimeProvider clock) : IJobScheduler
{
    public void EnqueueDiscoveryRun(Guid runId, Guid workspaceId) => queue.Add(RecordedJobType.Discovery, runId, workspaceId, clock.GetUtcNow());
    public void EnqueueCsvImport(Guid batchId, Guid workspaceId) => queue.Add(RecordedJobType.Csv, batchId, workspaceId, clock.GetUtcNow());
    public void EnqueueReplyClassification(Guid workspaceId, Guid messageId) => queue.Add(RecordedJobType.Classify, messageId, workspaceId, clock.GetUtcNow());
}

/// <summary>
/// Runs recorded jobs through the same job classes Hangfire calls, each in its own DI scope, oldest first. A failing job is
/// reported and doesn't stop the rest. Jobs that are enqueued while draining are drained too (unless filtered out).
/// </summary>
public sealed class RecordedJobRunner(RecordedJobQueue queue, IServiceScopeFactory scopes)
{
    public async Task<IReadOnlyList<JobRunResult>> RunPendingAsync(RecordedJobType? only, CancellationToken ct)
    {
        var results = new List<JobRunResult>();
        var skipped = new List<RecordedJob>();
        while (queue.TryTake(out var job))
        {
            if (only is not null && job.Type != only) { skipped.Add(job); continue; }
            results.Add(await RunAsync(job, ct));
        }
        foreach (var job in skipped) queue.Add(job.Type, job.Id, job.WorkspaceId, job.EnqueuedAt);   // put back, still in order
        return results;
    }

    private async Task<JobRunResult> RunAsync(RecordedJob job, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        try
        {
            switch (job.Type)
            {
                case RecordedJobType.Discovery:
                    await sp.GetRequiredService<ProcessLeadDiscoveryJob>().ExecuteAsync(job.Id, job.WorkspaceId, ct);
                    break;
                case RecordedJobType.Csv:
                    await sp.GetRequiredService<ProcessCsvImportJob>().ExecuteAsync(job.Id, job.WorkspaceId, ct);
                    break;
                case RecordedJobType.Classify:
                    await sp.GetRequiredService<ClassifyReplyJob>().ExecuteAsync(job.WorkspaceId, job.Id, ct);
                    break;
            }
            return new JobRunResult(job, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JobRunResult(job, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
