# Feature 14 — Automated Lead Discovery

**Branch:** `feature/lead-discovery`
**Sprint:** 1 (backend can start right after F11; UI after F13)
**Depends on:** F11 (`LeadIngestionService`, `Lead.Source`/`SourceRunId`/`EmailVerificationStatus`, tenant foundation), F13 for the UI (selection service, list filter by `sourceRunId`), F12 (shell).

## Goal
Find new leads that match a saved **Target Profile**, behind a swappable `ILeadSource`. This is also the first feature to use background jobs, so it sets up **Hangfire** for every later job (CSV import, sender, classification). Provider is undecided (Apollo.io vs People Data Labs): everything is built and tested against `FakeLeadSource`; the real provider is one Infrastructure class added once the vendor is chosen.

Terms (same as the parent plan): a *lead* is a person we might email; a *target profile* is search criteria; a *discovery run* executes a profile against the provider and produces leads through `LeadIngestionService`.

## Files to add/modify

### 14.0 — Hangfire setup

**Packages:** `ZenLead.Infrastructure` → `Hangfire.Core`, `Hangfire.SqlServer`; `ZenLead.Api` → `Hangfire.AspNetCore`.

**`ZenLead.Application/Abstractions/IJobScheduler.cs`** (new) — Application never sees Hangfire. Each job feature adds one method.
```csharp
namespace ZenLead.Application.Abstractions;

public interface IJobScheduler
{
    void EnqueueDiscoveryRun(Guid runId, Guid workspaceId);
    // F15 adds EnqueueCsvImport; F23 registers the recurring sender in Infrastructure; F26 adds EnqueueReplyClassification.
}
```

**`ZenLead.Infrastructure/Jobs/HangfireJobScheduler.cs`** (new)
```csharp
using Hangfire;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Jobs;

public class HangfireJobScheduler(IBackgroundJobClient client) : IJobScheduler
{
    public void EnqueueDiscoveryRun(Guid runId, Guid workspaceId)
        => client.Enqueue<ProcessLeadDiscoveryJob>(j => j.ExecuteAsync(runId, workspaceId, CancellationToken.None));
}
```
(Hangfire substitutes the shutdown token for `CancellationToken` arguments when it runs the job.)

**`ZenLead.Api/HangfireConfiguration.cs`** (new)
```csharp
using Hangfire;
using Hangfire.SqlServer;

namespace ZenLead.Api;

public static class HangfireConfiguration
{
    public static IServiceCollection AddZenLeadHangfire(this IServiceCollection services, IConfiguration config)
    {
        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(config.GetConnectionString("Default"), new SqlServerStorageOptions
            {
                SchemaName = "HangFire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(15),     // polite to the (free-tier) DB; F30 re-checks auto-pause behaviour
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5)
            }));
        services.AddHangfireServer(o => o.WorkerCount = 2);

        // 3 attempts, exponential-ish; after the last one the job stays visible in the dashboard's Failed list (our dead-letter view)
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute
        {
            Attempts = 3, DelaysInSeconds = [30, 120, 480], OnAttemptsExceeded = AttemptsExceededAction.Fail
        });
        return services;
    }
}
```
`Program.cs`: `if (builder.Configuration.GetValue("Hangfire:Enabled", true)) builder.Services.AddZenLeadHangfire(builder.Configuration);` (the switch lets endpoint tests boot without SQL Server). Register `IJobScheduler` → `HangfireJobScheduler` in the same `if`; a `NoopJobScheduler` otherwise.

**Dashboard authorisation.** The access token lives in memory and is sent as a bearer header, so a browser navigation to `/hangfire` carries **no credentials**. Solve with a short-lived cookie issued by an authenticated API call:

**`ZenLead.Api/Controllers/V1/OpsController.cs`** (new)
```csharp
[ApiController]
[Authorize]
[Route("api/v1/ops")]
public class OpsController(IConfiguration config, IDataProtectionProvider dp) : ControllerBase
{
    public const string CookieName = "zl_ops";

    [HttpGet("status")]
    public ActionResult<object> Status() => Ok(new { canOpenDashboard = IsOps() });

    [HttpPost("hangfire-session")]
    public IActionResult OpenDashboard()
    {
        if (!IsOps()) return Forbid();
        var protector = dp.CreateProtector("hangfire-dashboard");
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        Response.Cookies.Append(CookieName, protector.Protect(expires.ToUnixTimeSeconds().ToString()), new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/hangfire", Expires = expires
        });
        return NoContent();
    }

    private bool IsOps()
    {
        var email = User.FindFirstValue("email");
        var allowed = config.GetSection("Hangfire:AdminEmails").Get<string[]>() ?? [];
        return email is not null && allowed.Contains(email, StringComparer.OrdinalIgnoreCase);
    }
}
```
**`ZenLead.Api/HangfireDashboardAuthFilter.cs`** (new) — `IDashboardAsyncAuthorizationFilter`: read cookie `zl_ops` from `context.GetHttpContext().Request.Cookies`, `Unprotect` with the same protector, parse unix expiry, return `true` only when unexpired; any exception → `false`.
`Program.cs` after `UseAuthorization()`:
```csharp
app.MapHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new HangfireDashboardAuthFilter(app.Services.GetRequiredService<IDataProtectionProvider>())], IsReadOnlyFunc = _ => false });
```
Config: `Hangfire:AdminEmails` as a user-secrets array (`Hangfire:AdminEmails:0`). **`src/proxy.conf.js`** — add `"/hangfire"` to `context` (outside `/api`). Data Protection keys: default per-machine key ring is fine locally; in Azure (F30) a lost key ring only means the admin re-requests the cookie.

**Angular:** `core/layout/ops.service.ts` (`status()`, `openDashboard()` → POST then `window.open('/hangfire', '_blank')`); `shell.html` account menu shows **Job dashboard** only when `canOpenDashboard`.

### 14.1 — `ILeadSource` abstraction

**`ZenLead.Application/Abstractions/ILeadSource.cs`** (new)
```csharp
using ZenLead.Domain.Enums;

namespace ZenLead.Application.Abstractions;

/// <summary>All lists optional. Empty = "don't filter on this".</summary>
public record LeadSearchCriteria(
    IReadOnlyList<string> JobTitles, IReadOnlyList<string> Industries, IReadOnlyList<string> Countries,
    int? CompanySizeMin, int? CompanySizeMax, IReadOnlyList<string> CompanyDomains)
{
    public static LeadSearchCriteria Empty { get; } = new([], [], [], null, null, []);
    public bool IsEmpty => JobTitles.Count + Industries.Count + Countries.Count + CompanyDomains.Count == 0
                           && CompanySizeMin is null && CompanySizeMax is null;
}

public record DiscoveredLead(
    string ProviderId, string? FirstName, string? LastName, string? Email, string? Title,
    string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize,
    EmailVerificationStatus Verification)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
}

public record LeadSearchPage(IReadOnlyList<DiscoveredLead> Leads, string? NextCursor, int CreditsUsed);

public enum LeadSourceFailureKind { RateLimited, Unavailable, Unauthorized, OutOfCredits, InvalidResponse }

public class LeadSourceException(LeadSourceFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public LeadSourceFailureKind Kind { get; } = kind;
    public bool IsTransient => Kind is LeadSourceFailureKind.RateLimited or LeadSourceFailureKind.Unavailable;
}

public interface ILeadSource
{
    string Name { get; }                                   // "Fake" | "Apollo" | "Pdl" — stored on the run
    Task<LeadSearchPage> SearchAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct);
    Task<int?> GetRemainingCreditsAsync(CancellationToken ct);   // null when the provider can't say
}
```

**`ZenLead.Infrastructure/LeadSources/FakeLeadSource.cs`** (new) — deterministic, no network; also the dev default.
```csharp
public class FakeLeadSource : ILeadSource
{
    public string Name => "Fake";
    public int PageCostPerLead { get; set; } = 1;
    public Queue<Exception> FailuresToThrow { get; } = new();         // tests: enqueue failures for the next calls
    public List<(string? Cursor, int Limit)> Calls { get; } = [];

    // 200 deterministic people across 20 companies; honours Countries/JobTitles/Industries via simple Contains filters
    public Task<LeadSearchPage> SearchAsync(LeadSearchCriteria criteria, string? cursor, int limit, CancellationToken ct)
    {
        Calls.Add((cursor, limit));
        if (FailuresToThrow.TryDequeue(out var failure)) throw failure;

        var all = Dataset.Where(l => Matches(criteria, l)).ToList();
        var offset = cursor is null ? 0 : int.Parse(cursor);
        var page = all.Skip(offset).Take(limit).ToList();
        var next = offset + page.Count < all.Count ? (offset + page.Count).ToString() : null;
        return Task.FromResult(new LeadSearchPage(page, next, page.Count * PageCostPerLead));
    }

    public Task<int?> GetRemainingCreditsAsync(CancellationToken ct) => Task.FromResult<int?>(null);
    // Dataset: include rows with no email, a mix of Verified/Risky/Invalid/Unverified, and duplicated emails differing in case.
}
```

**Real provider (added after the vendor decision, not in the first PR):** `ZenLead.Infrastructure/LeadSources/ApolloLeadSource.cs` *or* `PdlLeadSource.cs` — a typed `HttpClient` (`AddHttpClient<ILeadSource, …>`) that (1) maps `LeadSearchCriteria` to the vendor's search request, (2) maps each result to `DiscoveredLead` including the vendor's email-status flag → `EmailVerificationStatus` (`verified`→Verified, `guessed/likely`→Risky, `invalid`→Invalid, absent→Unverified), (3) reads credits used from the response/usage endpoint, (4) maps HTTP 401/403→`Unauthorized`, 402/credit errors→`OutOfCredits`, 429→`RateLimited`, 5xx/timeouts→`Unavailable`, unparseable body→`InvalidResponse` (deserialise with `JsonSerializerDefaults.Web` and validate required fields — same lesson as F6/F26). **Field names, endpoints and credit rules come from the vendor's current API docs and your account tier (verify at implementation time, and re-read their terms on storing results — see parent plan §1).** Select the implementation in DI by `LeadSource:Provider` (`Fake` default in Development; startup fails fast in non-Development if the provider is `Fake` or the key is missing).

### 14.2 / 14.4 — Entities, options, migration

**`ZenLead.Domain/Entities/TargetProfile.cs`** (new)
```csharp
public class TargetProfile : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CriteriaJson { get; set; } = "{}";     // serialized LeadSearchCriteria (Web defaults)
    public Guid? SourceLeadId { get; set; }              // lead it was suggested from, if any (informational)
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
```

**`ZenLead.Domain/Entities/LeadDiscoveryRun.cs`** (new) + **`Enums/DiscoveryRunStatus.cs`**
```csharp
public enum DiscoveryRunStatus { Queued, Running, Completed, Failed, CapReached }

public class LeadDiscoveryRun : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? TargetProfileId { get; set; }            // null if the profile was deleted later (FK SetNull)
    public string CriteriaJson { get; set; } = "{}";      // snapshot at run time
    public string Provider { get; set; } = string.Empty;
    public int RequestedCount { get; set; }               // wanted NEW leads (imported), capped
    public int FoundCount { get; set; }                   // provider results seen (with or without email)
    public int ImportedCount { get; set; }
    public int SkippedDuplicateCount { get; set; }
    public int SkippedSuppressedCount { get; set; }
    public int NoEmailCount { get; set; }
    public int CreditsUsed { get; set; }
    public int PagesFetched { get; set; }
    public string? Cursor { get; set; }                   // resume point
    public DiscoveryRunStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
```

**`ZenLead.Application/Discovery/LeadSourceOptions.cs`** (new) — bound from config section `LeadSource`:
```csharp
public class LeadSourceOptions
{
    public string Provider { get; set; } = "Fake";
    public int MonthlyCreditCap { get; set; } = 500;        // per workspace, calendar month UTC
    public int MaxLeadsPerRun { get; set; } = 100;          // hard cap per run
    public int PageSize { get; set; } = 25;
    public int MaxPagesFactor { get; set; } = 5;            // stop after RequestedCount*factor/PageSize pages even if few imports (all-duplicates guard)
}
```

**EF configs** (`EntityConfigurations.cs`): `TargetProfileConfiguration` (`Name` 200, `CriteriaJson` `nvarchar(max)`, index `(WorkspaceId, Name)`, FK Workspace Restrict); `LeadDiscoveryRunConfiguration` (`Provider` 50, `FailureReason` 500, `Cursor` 200, index `(WorkspaceId, CreatedAt)`, index `(WorkspaceId, Status)`, FK Workspace Restrict, FK `TargetProfileId` → `TargetProfile` `OnDelete(SetNull)`). Add both `DbSet`s to `ZenLeadDbContext`. Migration **`AddTargetProfilesAndDiscoveryRuns`**.

### 14.3 — Profile from leads (pure logic)

**`ZenLead.Domain/Discovery/TargetProfileSuggester.cs`** (new)
```csharp
public record LeadProfileInput(string? Title, string? Industry, string? Country, string? CompanySize, LeadStatus Status);
public record SuggestedProfile(string Name, IReadOnlyList<string> JobTitles, IReadOnlyList<string> Industries,
                               IReadOnlyList<string> Countries, int? SizeMin, int? SizeMax);

public static class TargetProfileSuggester
{
    private const int TopN = 3;

    public static SuggestedProfile Suggest(IReadOnlyList<LeadProfileInput> leads, string? singleLeadName = null, bool onlyReplied = false)
    {
        var source = onlyReplied ? leads.Where(l => l.Status == LeadStatus.Replied).ToList() : leads.ToList();
        if (source.Count == 0) throw new InvalidOperationException("No leads to build a profile from.");

        var sizes = source.Select(l => CompanySizeParser.TryParse(l.CompanySize)).Where(r => r is not null).Select(r => r!.Value).ToList();
        return new SuggestedProfile(
            Name: source.Count == 1 && singleLeadName is not null ? $"Similar to {singleLeadName}" : $"Profile from {source.Count} leads",
            JobTitles: Top(source.Select(l => l.Title)),
            Industries: Top(source.Select(l => l.Industry)),
            Countries: Top(source.Select(l => l.Country)),
            SizeMin: sizes.Count == 0 ? null : sizes.Min(s => s.Min),
            SizeMax: sizes.Count == 0 ? null : sizes.Max(s => s.Max));
    }

    /// <summary>Most frequent non-blank values (case-insensitive), most frequent first, ties by first appearance.</summary>
    private static IReadOnlyList<string> Top(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
              .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
              .OrderByDescending(g => g.Count()).Take(TopN).Select(g => g.First()).ToList();
}
```
**`CompanySizeParser`** (same folder): `"51-200"` → (51,200), `"1000+"` → (1000, null→int.MaxValue handled as `null` max), `"500"` → (500,500), junk → `null`.

### 14.2 — Target profile CRUD & 14.3 endpoint

**`ZenLead.Application/Dtos/Discovery/DiscoveryDtos.cs`** (new)
```csharp
public record CriteriaDto(IReadOnlyList<string> JobTitles, IReadOnlyList<string> Industries, IReadOnlyList<string> Countries,
                          int? CompanySizeMin, int? CompanySizeMax, IReadOnlyList<string> CompanyDomains);
public record TargetProfileRequest(string Name, CriteriaDto Criteria);
public record TargetProfileResponse(Guid Id, string Name, CriteriaDto Criteria, Guid? SourceLeadId, DateTime CreatedAt);
public record SuggestFromLeadsRequest(IReadOnlyList<Guid> LeadIds, bool OnlyReplied = false);
public record SuggestedProfileResponse(string Name, CriteriaDto Criteria, Guid? SourceLeadId);
public record StartRunRequest(int MaxLeads);
public record DiscoveryRunResponse(Guid Id, Guid? TargetProfileId, string Provider, DiscoveryRunStatus Status, int RequestedCount,
    int FoundCount, int ImportedCount, int SkippedDuplicateCount, int SkippedSuppressedCount, int NoEmailCount, int CreditsUsed,
    string? FailureReason, DateTime CreatedAt, DateTime? FinishedAt);
public record CreditsResponse(int Used, int Cap, int Remaining, int? ProviderRemaining);
```
**Validators** (`Validation/Discovery/`): `TargetProfileRequestValidator` (name 1–200; each list ≤ 20 items, each item ≤ 100 chars; domains pass `CompanyKey.NormalizeDomain`; size min ≤ max, ≥ 0; **at least one criterion** — an empty profile would pull random people and burn credits); `SuggestFromLeadsRequestValidator` (1–500 ids); `StartRunRequestValidator` (`1..LeadSourceOptions.MaxLeadsPerRun`).

**`ZenLead.Application/Abstractions/ITargetProfileRepository.cs`**, **`IDiscoveryRunRepository.cs`** (new) — CRUD + `GetAsync(id)`, `ListAsync()`, `HasActiveRunAsync(workspaceId)`, `GetCreditsUsedThisMonthAsync(workspaceId, monthStartUtc)`, `UpdateAsync(run)`. **`ILeadRepository`** gains `GetByIdsAsync(Guid workspaceId, IReadOnlyCollection<Guid> ids)` (with `Company`). Implementations in `Infrastructure/Persistence/` follow `LeadRepository`; serialize criteria with `new JsonSerializerOptions(JsonSerializerDefaults.Web)`; a malformed stored JSON falls back to `LeadSearchCriteria.Empty`.

**`ZenLead.Api/Controllers/V1/TargetProfilesController.cs`** (new) — `[Authorize]`, `[Route("api/v1/target-profiles")]`:
- `GET` list, `POST` create, `PUT {id}`, `DELETE {id}` — each with the workspace-claim check and the same-404-for-other-tenant rule; `CreatedBy` from `this.UserId()` (add `UserId()` to `CurrentUser.cs`: `Guid.TryParse(User.FindFirstValue("sub"))`).
- `POST from-leads` (declare before `{id}` routes): load leads with `GetByIdsAsync(workspaceId, ids)`; if the workspace returns fewer ids than requested, ignore the missing (other tenants' ids simply don't resolve); empty → 404; map to `LeadProfileInput` → `TargetProfileSuggester.Suggest(...)` → `SuggestedProfileResponse` (**not saved**). `onlyReplied` with zero replied leads → 400 "None of the selected leads have replied."
- `POST {id}/runs` `[EnableRateLimiting("discovery")]` → `StartDiscoveryRunUseCase` (below) → `202 Accepted` + `DiscoveryRunResponse`, `Location: /api/v1/discovery/runs/{id}`.

**`ZenLead.Api/Controllers/V1/DiscoveryController.cs`** (new) — `GET runs/{id}`, `GET runs` (latest 20, optional `targetProfileId`), `GET credits`.

**`ZenLead.Api/RateLimiting.cs`** — add `DiscoveryPolicy = "discovery"`, 5 permits/minute per workspace (same partition shape as compose); register in `Program.cs`.

### 14.5 / 14.6 — Starting and processing a run

**`ZenLead.Application/UseCases/Discovery/StartDiscoveryRunUseCase.cs`** (new)
```csharp
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

    internal static DateTime MonthStart(DateTimeOffset now) => new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
}
```
`StartRunResult`: `NotFound | AlreadyRunning | CapReached | Started(run)` → controller maps to 404 / 409 / 409 (`{ message: "Monthly discovery credit cap reached." }`) / 202.

**`ZenLead.Application/UseCases/Discovery/ProcessDiscoveryRunUseCase.cs`** (new) — all the logic, so it is testable with fakes; `ProcessLeadDiscoveryJob` is a 10-line wrapper.
```csharp
public class ProcessDiscoveryRunUseCase(
    IDiscoveryRunRepository runs, ILeadSource source, LeadIngestionService ingestion,
    LeadSourceOptions options, TimeProvider clock, ILogger<ProcessDiscoveryRunUseCase> logger)
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30)];
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);   // tests replace with a no-op

    public async Task ExecuteAsync(Guid runId, Guid workspaceId, CancellationToken ct)
    {
        var run = await runs.GetForJobAsync(runId, workspaceId, ct);          // ForWorkspace(...) — no HTTP context here
        if (run is null || run.Status is DiscoveryRunStatus.Completed or DiscoveryRunStatus.CapReached) return;  // idempotent re-run

        var criteria = LeadCriteriaJson.Deserialize(run.CriteriaJson);
        run.Status = DiscoveryRunStatus.Running; run.StartedAt ??= clock.GetUtcNow().UtcDateTime; run.FailureReason = null;
        await runs.UpdateAsync(run, ct);

        var maxPages = Math.Max(1, run.RequestedCount * options.MaxPagesFactor / options.PageSize);
        var used = await runs.GetCreditsUsedThisMonthAsync(workspaceId, StartDiscoveryRunUseCase.MonthStart(clock.GetUtcNow()), ct);

        while (run.ImportedCount < run.RequestedCount && run.PagesFetched < maxPages)
        {
            // cost guard: checked BEFORE each provider page; used already includes this run's earlier pages (persisted per page)
            if (used >= options.MonthlyCreditCap) { await FinishAsync(run, DiscoveryRunStatus.CapReached, "Monthly discovery credit cap reached.", ct); return; }

            LeadSearchPage page;
            try { page = await FetchWithRetryAsync(criteria, run.Cursor, Math.Min(options.PageSize, run.RequestedCount - run.ImportedCount + 5), ct); }
            catch (LeadSourceException ex)
            {
                await FinishAsync(run, DiscoveryRunStatus.Failed, Describe(ex), ct);
                return;                               // handled failure: no Hangfire retry; the user can start a new run (dedupe makes it cheap)
            }

            var withEmail = page.Leads.Where(l => !string.IsNullOrWhiteSpace(l.Email)).ToList();
            var summary = await ingestion.IngestAsync(workspaceId,
                withEmail.Select(l => new CandidateLead(l.FullName, l.Email, l.Title, l.CompanyName, l.CompanyDomain,
                                                        l.Industry, l.Country, l.CompanySize, l.Verification)).ToList(),
                new IngestionSource(LeadSource.Discovery, run.Id), ct);

            // trim: never import more than requested — ingestion already committed this page, so cap the *counted* number and stop
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
    // FetchWithRetryAsync: up to 3 retries on IsTransient with RetryDelays; non-transient → rethrow immediately.
    // Describe(ex): friendly, no provider internals ("Provider rate limit — try again later", "Provider credits exhausted", …).
}
```
> **Overshoot rule:** the page request asks for at most `remaining + 5` and ingestion commits the whole page, so a run can import slightly more than `RequestedCount` (by < 5 leads). That is deliberate (simpler and idempotent); the UI shows the real imported number. If exact counts matter later, slice `withEmail` to `remaining` before ingesting.

**`ZenLead.Infrastructure/Jobs/ProcessLeadDiscoveryJob.cs`** (new)
```csharp
public class ProcessLeadDiscoveryJob(ProcessDiscoveryRunUseCase useCase)
{
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task ExecuteAsync(Guid runId, Guid workspaceId, CancellationToken ct) => useCase.ExecuteAsync(runId, workspaceId, ct);
}
```
Register in `Program.cs`: `LeadSourceOptions` (bound), `ILeadSource`, both repositories, both use cases, `ProcessLeadDiscoveryJob`, `TimeProvider.System`.

**Credits endpoint:** `GET /discovery/credits` → `used` (this month, from runs), `cap`, `remaining = max(0, cap-used)`, `providerRemaining` from `source.GetRemainingCreditsAsync` (swallow `LeadSourceException` → `null`, 2 s timeout).

### 14.7 — Email quality gate (consumed by F22)

**`ZenLead.Domain/Leads/EnrollmentEligibility.cs`** (new)
```csharp
public enum EligibilityKind { Eligible, EligibleWithWarning, Blocked }
public record Eligibility(EligibilityKind Kind, string? Reason);

public static class EnrollmentEligibility
{
    public static Eligibility Check(LeadStatus status, EmailVerificationStatus verification) =>
        LeadStatusRules.IsSuppressed(status) ? new(EligibilityKind.Blocked, $"Lead is {status}")
        : verification == EmailVerificationStatus.Invalid ? new(EligibilityKind.Blocked, "Email marked invalid")
        : verification is EmailVerificationStatus.Unverified or EmailVerificationStatus.Risky
            ? new(EligibilityKind.EligibleWithWarning, $"Email is {verification}")
        : new(EligibilityKind.Eligible, null);
}
```
F22's enroll endpoint skips `Blocked` and reports `EligibleWithWarning` counts; F24's confirm step shows them. (`Replied` leads are allowed here; F22 decides whether to skip leads already in another active campaign.)

### Angular

**`features/leads/profiles/`** (new, inside `LeadsModule`; routes added **before** `:id`: `profiles`, `profiles/new`, `profiles/:id`)
- `profiles.models.ts` — `Criteria`, `TargetProfile`, `SuggestedProfile`, `DiscoveryRun`, `Credits`, `RunStatus` mirroring the DTOs.
- `profiles.service.ts` — `list/create/update/delete`, `suggestFromLeads(ids, onlyReplied)`, `startRun(profileId, maxLeads)`, `getRun(id)`, `credits()`.
- `profile-draft.service.ts` — holds a `SuggestedProfile` between "Create from lead" and the editor (`history.state` is lost on refresh and is awkward in a zoneless router; a root service is simpler; refresh just drops the draft with a snackbar).
- `target-profiles-list` — table (name, criteria summary chips, created), actions Edit / Run / Delete; **credit meter** (`mat-progress-bar` determinate, label "120 / 500 discovery credits used this month"; turns warn colour ≥ 90% with the text, not colour alone); latest runs list under it.
- `target-profile-edit` — reactive form: name; **chip inputs** for job titles, industries, countries and company domains (`MatChipGrid` with `matChipInputFor`, add on Enter/comma, validators matching the server: ≤ 20 items); company size min/max numeric; save → `POST`/`PUT`. Banner when opened from a draft ("Suggested from <lead>; review and save").
- `run-discovery-dialog` — max-leads input (1…cap from a config value returned by `credits()` as `maxLeadsPerRun`; add that field to `CreditsResponse`), shows remaining credits, **Start** → `startRun` → progress view polling `getRun` every 2 s (`timer(0, 2000).pipe(switchMap(getRun), takeWhile(r => !terminal(r), true))`, unsubscribe on destroy) with a spinner and live counts; terminal state summary: *found / imported / duplicates / suppressed / no email*, status badge (`Completed`, `Cap reached`, `Failed — <reason>`), and **View imported leads** → `/leads?sourceRunId=<id>`.
- Entry points: leads-list header link **Target profiles**; lead-detail button **Create target profile from this lead** → `suggestFromLeads([id])` → draft → editor; leads-list selection bar button **Create target profile** (flag `discovery` on) → `suggestFromLeads(selection.selected)`, with an **only leads that replied** checkbox when ≥ 1 selected lead has status `Replied`.
- `environments/environment.ts`: `features.discovery = true`.
- Material: `MatChipsModule` (already added in F13), `MatProgressBarModule`, `MatSnackBarModule`, `MatTabsModule` (optional) → `SharedModule`.
- No proxy change for `/api/...`; **`/hangfire` added** (see 14.0).

## Tests (priority per 14.9)
All backend tests use `FakeLeadSource`, fakes for repositories, and `TestDb` where persistence matters.
- **`TargetProfileSuggesterTests`** — single lead copies title/industry/country/size; several leads → top-3 by frequency, case-insensitive grouping, deterministic tie-break; size range spans min..max, unparseable sizes ignored; `onlyReplied` filters, and throws when none; **`CompanySizeParserTests`** (`"51-200"`, `"1000+"`, `"500"`, `""`, junk).
- **`ProcessDiscoveryRunUseCaseTests`**:
  - results without email dropped and counted in `NoEmailCount`; existing lead → `SkippedDuplicate`; `Unsubscribed`/`Bounced` existing → `SkippedSuppressed`; imported leads carry `Source=Discovery`, `SourceRunId`, provider verification flag; provider-`Invalid` rows not imported.
  - **idempotent re-run**: completed run is a no-op; a run interrupted after page 1 (cursor persisted) resumes from the cursor and imports no duplicates; running the same run twice yields identical lead count.
  - **cap**: with `MonthlyCreditCap` 30 and 25 credits per page, run ends `CapReached` after the second page boundary check and never calls the provider a third time; a workspace already at the cap can't start a run (`StartRunResult.CapReached`); another workspace's usage doesn't count (cross-workspace).
  - **failures**: transient failure then success → retries and completes (assert `Delay` was called with the backoff sequence); transient ×4 → `Failed` with friendly reason and no unhandled exception; `OutOfCredits`/`Unauthorized` → `Failed` immediately, no retry; `InvalidResponse` → `Failed`.
  - stops at `RequestedCount` (within the documented overshoot) and at `maxPages` when every result is a duplicate.
- **`StartDiscoveryRunUseCaseTests`** — other tenant's profile → `NotFound`; second start while active → `AlreadyRunning`; job enqueued exactly once, after the run row exists; `maxLeads` clamped.
- **`TargetProfilesControllerTests`** — CRUD happy path; **cross-workspace**: B cannot get/update/delete/run A's profile (404); `from-leads` ignores another workspace's lead ids; empty-criteria profile rejected.
- **`EnrollmentEligibilityTests`** — table of status × verification.
- **`HangfireDashboardAuthFilterTests`** — no cookie → false, expired → false, tampered → false, valid → true; `OpsController` non-admin → 403.
- **`LeadDiscoveryRunPersistenceTests`** (SQLite) — monthly credits sum respects month boundary and workspace.
- Angular: `profiles.service.spec.ts` (paths/payloads), `target-profile-edit.spec.ts` (chip add/remove, size validation, at-least-one-criterion), `run-discovery-dialog.spec.ts` (polling stops on terminal state, summary link carries `sourceRunId`).

## Not in this feature
Scheduled/recurring runs, auto-enrolment of discovered leads, a standalone email verifier (`IEmailVerifier`), phone discovery, resuming a failed run from the UI, per-lead enrichment calls. One real run against the chosen provider's test tier is a **Gate 2 prerequisite**, tracked in the Sprint 1 exit criteria.

## Verification
- `dotnet ef migrations add AddTargetProfilesAndDiscoveryRuns …` → `database update`; start the API, confirm Hangfire created its `HangFire` schema in LocalDB and `/hangfire` is **401/blocked without the cookie**, and opens after "Job dashboard" is clicked by an email listed in `Hangfire:AdminEmails`.
- With `LeadSource:Provider=Fake`: create a profile (countries `US`), run 20 leads → leads appear, tagged `Discovery`, filtered by run link; run again → mostly duplicates, no new rows; set `MonthlyCreditCap` to 10 → run ends *Cap reached*; kill the API mid-run, restart → job resumes (Hangfire re-queues after the invisibility timeout) without duplicates.
- Create a profile from one lead and from three leads (one `Replied`) in the UI; edit and save.
- `dotnet test`, `ng test`.
