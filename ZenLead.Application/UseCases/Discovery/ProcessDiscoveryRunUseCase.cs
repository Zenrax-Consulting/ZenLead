using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Discovery;
using ZenLead.Application.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Application.UseCases.Discovery;

/// <summary>All the discovery-run logic, so it is testable with fakes; the Hangfire job is a thin wrapper around it.</summary>
public class ProcessDiscoveryRunUseCase(
    IDiscoveryRunRepository runs, ILeadSource source, LeadIngestionService ingestion,
    LeadSourceOptions options, TimeProvider clock, ILogger<ProcessDiscoveryRunUseCase> logger)
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30)];
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);   // tests replace with a no-op

    public async Task ExecuteAsync(Guid runId, Guid workspaceId, CancellationToken ct)
    {
        var run = await runs.GetForJobAsync(runId, workspaceId, ct);          // explicit workspace scope — no HTTP context here
        if (run is null || run.Status is DiscoveryRunStatus.Completed or DiscoveryRunStatus.CapReached) return;  // idempotent re-run

        var criteria = LeadCriteriaJson.Deserialize(run.CriteriaJson);
        run.Status = DiscoveryRunStatus.Running; run.StartedAt ??= clock.GetUtcNow().UtcDateTime; run.FailureReason = null;
        await runs.UpdateAsync(run, ct);

        var maxPages = Math.Max(1, run.RequestedCount * options.MaxPagesFactor / options.PageSize);
        var used = await runs.GetCreditsUsedThisMonthAsync(workspaceId, StartDiscoveryRunUseCase.MonthStart(clock.GetUtcNow()), ct);

        while (run.ImportedCount < run.RequestedCount && run.PagesFetched < maxPages)
        {
            // cost guard: checked BEFORE each provider page; used already includes this run's earlier pages (persisted per page)
            if (used >= options.MonthlyCreditCap)
            {
                await FinishAsync(run, DiscoveryRunStatus.CapReached, "Monthly discovery credit cap reached.", ct);
                return;
            }

            LeadSearchPage page;
            try { page = await FetchWithRetryAsync(criteria, run.Cursor, Math.Min(options.PageSize, run.RequestedCount - run.ImportedCount + 5), ct); }
            catch (LeadSourceException ex)
            {
                logger.LogWarning(ex, "Discovery run {RunId} failed with {Kind}", run.Id, ex.Kind);
                await FinishAsync(run, DiscoveryRunStatus.Failed, Describe(ex), ct);
                return;                               // handled failure: no Hangfire retry; the user can start a new run (dedupe makes it cheap)
            }

            var withEmail = page.Leads.Where(l => !string.IsNullOrWhiteSpace(l.Email)).ToList();
            var summary = await ingestion.IngestAsync(workspaceId,
                withEmail.Select(l => new CandidateLead(l.FullName, l.Email, l.Title, l.CompanyName, l.CompanyDomain,
                                                        l.Industry, l.Country, l.CompanySize, l.Verification)).ToList(),
                new IngestionSource(LeadSource.Discovery, run.Id), ct);

            // ingestion already committed this page, so the counters follow what actually landed (see overshoot rule in the plan)
            run.FoundCount += page.Leads.Count;
            run.NoEmailCount += page.Leads.Count - withEmail.Count;
            run.ImportedCount += summary.Imported;
            run.SkippedDuplicateCount += summary.Duplicates;
            run.SkippedSuppressedCount += summary.Suppressed;
            run.CreditsUsed += page.CreditsUsed; used += page.CreditsUsed;
            run.PagesFetched++; run.Cursor = page.NextCursor;
            await runs.UpdateAsync(run, ct);                              // resumable: counters + cursor persisted per page

            if (page.NextCursor is null) break;
        }
        await FinishAsync(run, DiscoveryRunStatus.Completed, null, ct);
    }

    private async Task<LeadSearchPage> FetchWithRetryAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await source.SearchAsync(criteria, cursor, limit, ct); }
            catch (LeadSourceException ex) when (ex.IsTransient && attempt < RetryDelays.Length)
            {
                await Delay(RetryDelays[attempt], ct);
            }
        }
    }

    private async Task FinishAsync(LeadDiscoveryRun run, DiscoveryRunStatus status, string? reason, CancellationToken ct)
    {
        run.Status = status;
        run.FailureReason = reason;
        run.FinishedAt = clock.GetUtcNow().UtcDateTime;
        await runs.UpdateAsync(run, ct);
    }

    private static string Describe(LeadSourceException ex) => ex.Kind switch
    {
        LeadSourceFailureKind.RateLimited => "Provider rate limit — try again later.",
        LeadSourceFailureKind.Unavailable => "Provider is unavailable — try again later.",
        LeadSourceFailureKind.Unauthorized => "Provider credentials were rejected.",
        LeadSourceFailureKind.OutOfCredits => "Provider credits exhausted.",
        _ => "Provider returned an unexpected response."
    };
}
