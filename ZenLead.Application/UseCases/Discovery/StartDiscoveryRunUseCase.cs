using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Discovery;

public enum StartRunOutcome { NotFound, AlreadyRunning, CapReached, Started }

public class StartRunResult
{
    public StartRunOutcome Outcome { get; private init; }
    public LeadDiscoveryRun? Run { get; private init; }

    public static StartRunResult NotFound { get; } = new() { Outcome = StartRunOutcome.NotFound };
    public static StartRunResult AlreadyRunning { get; } = new() { Outcome = StartRunOutcome.AlreadyRunning };
    public static StartRunResult CapReached { get; } = new() { Outcome = StartRunOutcome.CapReached };
    public static StartRunResult Started(LeadDiscoveryRun run) => new() { Outcome = StartRunOutcome.Started, Run = run };
}

public class StartDiscoveryRunUseCase(
    ITargetProfileRepository profiles, IDiscoveryRunRepository runs, ILeadSource source,
    IJobScheduler scheduler, LeadSourceOptions options, TimeProvider clock)
{
    public async Task<StartRunResult> ExecuteAsync(Guid workspaceId, Guid userId, Guid profileId, int maxLeads, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(profileId, ct);
        if (profile is null || profile.WorkspaceId != workspaceId) return StartRunResult.NotFound;

        if (await runs.HasActiveRunAsync(workspaceId, ct)) return StartRunResult.AlreadyRunning;   // one at a time per workspace

        var used = await runs.GetCreditsUsedThisMonthAsync(workspaceId, MonthStart(clock.GetUtcNow()), ct);
        if (used >= options.MonthlyCreditCap) return StartRunResult.CapReached;

        var run = new LeadDiscoveryRun
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, TargetProfileId = profile.Id, CriteriaJson = profile.CriteriaJson,
            Provider = source.Name, RequestedCount = Math.Clamp(maxLeads, 1, options.MaxLeadsPerRun),
            Status = DiscoveryRunStatus.Queued, CreatedBy = userId, CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        await runs.AddAsync(run, ct);
        scheduler.EnqueueDiscoveryRun(run.Id, workspaceId);       // after the row is committed, so the job always finds it
        return StartRunResult.Started(run);
    }

    public static DateTime MonthStart(DateTimeOffset now) => new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
