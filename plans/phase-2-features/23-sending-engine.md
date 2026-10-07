# Feature 23 — Sending Engine

**Branch:** `feature/sendgrid-sending-engine`
**Sprint:** 3
**Depends on:** F18 (`IEmailSender`, `EmailOptions`), F22 (campaigns, enrollments, `SendScheduler`, `EnrollmentRules`, `TemplateRenderer`, `IUnsubscribeTokens`), F14.0 (Hangfire), F11 (`ForWorkspace`, `LeadStatusRules`), F17 (verified sending domain — or the single-sender fallback).

## Goal
A Hangfire recurring job that sends due campaign emails safely: idempotent (never double-send), capped (per campaign, per workspace, per minute), suppression-aware, optionally AI-personalised with graceful fallback, with failures isolated per enrollment. Plus the public unsubscribe flow, the persisted AI usage log with a monthly soft cap, and the global suppression list.

## Design decisions
- **At-most-once delivery.** Email can't be un-sent and SendGrid has no idempotency key, so on any uncertainty we skip rather than risk emailing a prospect twice. The `EmailMessage` row is inserted (`Queued`) **before** the SendGrid call, and a **unique `(EnrollmentId, StepId)`** index makes a second insert impossible. A `Queued` row found later (process died mid-send) is **not retried**: it becomes `Failed("Uncertain…")` and the enrollment moves on. Documented, tested, and visible to the user in the enrollment table.
- **Retryable provider errors don't leave a trail**: the `Queued` row is deleted, `SendAttempts++`, and `NextSendAt` is pushed out with backoff (5 min, 30 min, 2 h); the third failure marks the enrollment `Failed`. Non-retryable errors fail the enrollment immediately.
- **Per-enrollment isolation**: each enrollment is its own try/catch and its own save; one bad row never stops the batch.
- **AI never blocks a send.** If personalisation is off, the budget is exhausted, or the model fails, the plain rendered template is sent and the fallback is logged.
- **Suppression list is a table, not just lead status**: `SuppressedEmail` survives lead deletion and covers addresses with no lead (spam complaints, unsubscribe replies). It is checked at **ingestion, enrollment and send**.
- **Unsubscribe is a two-step page**: the email link opens an Angular page that `POST`s, so mail scanners that prefetch links cannot unsubscribe people (or, worse, hide real engagement). The `List-Unsubscribe` header points at the same `POST` endpoint with `List-Unsubscribe-Post: List-Unsubscribe=One-Click` (RFC 8058), which Gmail/Yahoo use for the native "Unsubscribe" button.
- **Dev safety net**: `Sending:RecipientAllowList` (domains/addresses). When non-empty, anything else is blocked — so a dev database full of imported prospects can never be mailed by accident.

## Files to add/modify

### Domain

**`Entities/EmailMessage.cs`** + **`Enums/EmailMessageStatus.cs`** (new; `ITenantEntity`)
```csharp
public enum EmailMessageStatus { Queued, Sent, Delivered, Bounced, Dropped, Failed }

public class EmailMessage : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid StepId { get; set; }
    public Guid LeadId { get; set; }
    public string? ProviderMessageId { get; set; }          // SendGrid X-Message-Id (event correlation fallback; primary is the emailMessageId custom arg)
    public string? SmtpMessageId { get; set; }              // real RFC Message-ID, filled by F28 events if useful for threading (F25 fallback)
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;        // the text actually sent (incl. footer)
    public bool UsedAi { get; set; }
    public EmailMessageStatus Status { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? BouncedAt { get; set; }
    public string? Error { get; set; }                      // ≤ 500 chars, no recipient data
}
```
Config: `Subject` 300, `Body` `nvarchar(max)`, `Error` 500; **unique `(EnrollmentId, StepId)`**; indexes `(WorkspaceId, CampaignId, SentAt)` (also what F28 wants), `(WorkspaceId, QueuedAt)`, `ProviderMessageId`; FK Enrollment **Restrict** (history survives).

**`Entities/SuppressedEmail.cs`** (new; `ITenantEntity`): `Id, WorkspaceId, Email (lowercase, 256), Reason (SuppressionReason: Unsubscribed | Bounced | SpamReport | Manual), CreatedAt, SourceLeadId?`; **unique `(WorkspaceId, Email)`**.

**`Entities/CampaignEnrollment.cs`** (F22, modified) — add `public int SendAttempts { get; set; }`.

**`Entities/AiUsageLog.cs`** (Phase 1, modified — **extended, not renamed**: the parent plan's `AiGenerationLog` is this same table; renaming would churn Phase 1 code and tests for no gain) — add:
```csharp
public AiPurpose Purpose { get; set; } = AiPurpose.Compose;     // Compose | Personalise | ClassifyReply (F26)
public Guid? CampaignId { get; set; }
public Guid? EnrollmentId { get; set; }
public bool Succeeded { get; set; } = true;
```
(`AiPurpose` enum in Domain.) Existing rows backfill to `Compose`/`true` through the column defaults in the migration.

**`Scheduling/ReplyAddress.cs`** (new, pure) — the Reply-To codec F25 decodes:
```csharp
public static class ReplyAddress
{
    private const string Prefix = "r-";
    public static string Encode(Guid enrollmentId, string inboundDomain) => $"{Prefix}{enrollmentId:N}@{inboundDomain}";
    public static bool TryDecode(string? address, string inboundDomain, out Guid enrollmentId)
    {
        enrollmentId = Guid.Empty;
        if (address is null) return false;
        var at = address.LastIndexOf('@');
        if (at <= 0 || !address[(at + 1)..].Equals(inboundDomain, StringComparison.OrdinalIgnoreCase)) return false;
        var local = address[..at];
        return local.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(local[Prefix.Length..], "N", out enrollmentId);
    }
}
```
A GUID is 122 random bits: unguessable, so no MAC needed in the address (F25 still validates that the sender matches the lead).

### Application

**`Sending/SendingOptions.cs`** (new; section `Sending`): `Enabled = true`, `WorkspaceDailyCap = 50` (new-domain warm-up; raise by config as reputation builds), `MaxPerWorkspacePerRun = 20` (= per-minute throttle at a 1-minute cadence), `BatchSize = 200`, `MaxAttempts = 3`, `BackoffMinutes = [5, 30, 120]`, `StaleQueuedMinutes = 10`, `RecipientAllowList = []`.

**`Abstractions/ISenderStore.cs`** (new) — every persistence call the sender needs, so `SendDueEmailsUseCase` is testable with an in-memory fake. EF implementation (`Infrastructure/Persistence/SenderStore.cs`) uses `ForWorkspace()` everywhere except `GetDueAsync`, which is the one deliberate cross-workspace read (`IgnoreQueryFilters([Tenant])`, only IDs leave it).
```csharp
public record DueEnrollment(Guid EnrollmentId, Guid WorkspaceId, Guid CampaignId, DateTime NextSendAt);

public class SendContext
{
    public required CampaignEnrollment Enrollment { get; init; }       // tracked
    public required Campaign Campaign { get; init; }                   // with Steps
    public required Lead Lead { get; init; }                           // with Company
    public required string WorkspaceTimeZone { get; init; }
}

public interface ISenderStore
{
    Task<IReadOnlyList<DueEnrollment>> GetDueAsync(DateTime nowUtc, int limit, CancellationToken ct);   // Active campaign + Active enrollment + NextSendAt <= now, oldest first
    Task<SendContext?> LoadAsync(Guid workspaceId, Guid enrollmentId, CancellationToken ct);
    Task<int> CountMessagesSinceAsync(Guid workspaceId, Guid? campaignId, DateTime sinceUtc, CancellationToken ct);   // Queued+Sent+Delivered+Bounced (not Failed/Dropped-before-send)
    Task<EmailMessage?> FindMessageAsync(Guid enrollmentId, Guid stepId, CancellationToken ct);
    Task<bool> TryInsertQueuedAsync(EmailMessage message, CancellationToken ct);                       // false on unique violation
    Task DeleteMessageAsync(Guid messageId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);                                                              // persists tracked changes (enrollment, lead, message)
    Task<bool> CampaignHasLiveEnrollmentsAsync(Guid workspaceId, Guid campaignId, CancellationToken ct);
}

public interface ISuppressionList
{
    Task<bool> ContainsAsync(Guid workspaceId, string email, CancellationToken ct);
    Task<IReadOnlySet<string>> FilterSuppressedAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct);   // for ingestion/enrollment batches
    Task AddAsync(Guid workspaceId, string email, SuppressionReason reason, Guid? leadId, CancellationToken ct);                       // upsert
}
```
**Touch-ups elsewhere for the suppression list:** F11 `LeadIngestionService` calls `ISuppressionList.FilterSuppressedAsync` after the existing-lead lookup and reports `Suppressed("On suppression list")` (F14.5's "added to the ingestion check then"); F22 `EnrollLeadsUseCase` skips suppressed emails (counted in `SkippedSuppressed`). Both get a test each.

**`UseCases/Sending/SendDueEmailsUseCase.cs`** (new) — the engine
```csharp
public class SendDueEmailsUseCase(ISenderStore store, ISuppressionList suppression, IEmailSender sender, IEmailComposer composer,
    IAiBudget budget, ITokenUsageTracker usage, AiPricing pricing, IUnsubscribeTokens unsubscribeTokens,
    IOptions<SendingOptions> sending, IOptions<EmailOptions> email, IOptions<AppOptions> app, TimeProvider clock, ILogger<SendDueEmailsUseCase> logger)
{
    public async Task<SendRunSummary> ExecuteAsync(CancellationToken ct)
    {
        var o = sending.Value;
        var summary = new SendRunSummary();
        var due = await store.GetDueAsync(clock.GetUtcNow().UtcDateTime, o.BatchSize, ct);
        var sentThisRun = new Dictionary<Guid, int>();                                  // per workspace: the per-minute throttle
        var workspaceBudget = new Dictionary<Guid, int>();                              // remaining daily cap, computed lazily
        var touchedCampaigns = new HashSet<(Guid Workspace, Guid Campaign)>();

        foreach (var item in due)
        {
            if (ct.IsCancellationRequested) break;
            if (sentThisRun.GetValueOrDefault(item.WorkspaceId) >= o.MaxPerWorkspacePerRun) { summary.Throttled++; continue; }
            try
            {
                var outcome = await ProcessAsync(item, workspaceBudget, ct);
                summary.Count(outcome);
                if (outcome == SendOutcome.Sent) sentThisRun[item.WorkspaceId] = sentThisRun.GetValueOrDefault(item.WorkspaceId) + 1;
                touchedCampaigns.Add((item.WorkspaceId, item.CampaignId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.Errors++;                                                       // leave the row for the next run; never stop the batch
                logger.LogError(ex, "Sending failed for enrollment {EnrollmentId}", item.EnrollmentId);
            }
        }
        foreach (var (workspaceId, campaignId) in touchedCampaigns) await CompleteCampaignIfDoneAsync(workspaceId, campaignId, ct);
        return summary;
    }

    private async Task<SendOutcome> ProcessAsync(DueEnrollment item, Dictionary<Guid, int> workspaceBudget, CancellationToken ct)
    {
        var ctx = await store.LoadAsync(item.WorkspaceId, item.EnrollmentId, ct);
        if (ctx is null || !EnrollmentRules.IsDue(ctx.Enrollment, clock.GetUtcNow().UtcDateTime) || ctx.Campaign.Status != CampaignStatus.Active) return SendOutcome.Skipped;
        var (e, campaign, lead) = (ctx.Enrollment, ctx.Campaign, ctx.Lead);
        var now = clock.GetUtcNow().UtcDateTime;
        var step = campaign.Steps.FirstOrDefault(s => s.Order == e.NextStepOrder);
        if (step is null) { Fail(e, "Step no longer exists."); await store.SaveAsync(ct); return SendOutcome.Failed; }

        // 0. Crash-recovery / idempotency: a message for (enrollment, step) already exists
        var existing = await store.FindMessageAsync(e.Id, step.Id, ct);
        if (existing is not null) return await RepairAsync(ctx, step, existing, now, ct);

        // 1. Stop conditions and suppression — checked every send, not just at enrollment
        if (lead.Status == LeadStatus.Replied) { EnrollmentRules.ApplyStop(e, StopEvent.Replied); await store.SaveAsync(ct); return SendOutcome.Stopped; }
        if (LeadStatusRules.IsSuppressed(lead.Status) || await suppression.ContainsAsync(item.WorkspaceId, lead.Email, ct))
        { EnrollmentRules.ApplyStop(e, lead.Status == LeadStatus.Bounced ? StopEvent.Bounced : StopEvent.Unsubscribed); await store.SaveAsync(ct); return SendOutcome.Stopped; }
        if (lead.EmailVerificationStatus == EmailVerificationStatus.Invalid) { Fail(e, "Email address marked invalid."); await store.SaveAsync(ct); return SendOutcome.Failed; }
        if (!AllowedByRecipientList(lead.Email)) { Fail(e, "Blocked by the recipient allow-list (non-production safety)."); await store.SaveAsync(ct); return SendOutcome.Failed; }

        // 2. Caps: campaign (workspace-local day) and workspace warm-up cap
        var zone = CampaignWindowFactory.For(campaign, ctx.WorkspaceTimeZone).Zone;
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTimeFromUtc(now, zone).Date, zone);
        if (await store.CountMessagesSinceAsync(item.WorkspaceId, campaign.Id, dayStartUtc, ct) >= campaign.DailySendCap) return SendOutcome.CapReached;
        if (!workspaceBudget.TryGetValue(item.WorkspaceId, out var left))
            left = Math.Max(0, sending.Value.WorkspaceDailyCap - await store.CountMessagesSinceAsync(item.WorkspaceId, null, dayStartUtc, ct));
        if (left <= 0) { workspaceBudget[item.WorkspaceId] = 0; return SendOutcome.CapReached; }

        // 3. Content (template, then optional AI personalisation with fallback)
        var rendered = await BuildContentAsync(ctx, step, ct);
        if (rendered.Missing.Count > 0) { Fail(e, $"Missing value for {string.Join(", ", rendered.Missing.Select(t => "{{" + t + "}}"))}."); await store.SaveAsync(ct); return SendOutcome.Failed; }

        // 4. Insert BEFORE sending (the unique index is the double-send guard)
        var message = new EmailMessage { Id = Guid.NewGuid(), WorkspaceId = item.WorkspaceId, CampaignId = campaign.Id, EnrollmentId = e.Id, StepId = step.Id, LeadId = lead.Id,
                                         Subject = rendered.Subject, Body = rendered.TextBody, UsedAi = rendered.UsedAi, Status = EmailMessageStatus.Queued, QueuedAt = now };
        if (!await store.TryInsertQueuedAsync(message, ct)) return SendOutcome.Skipped;           // another runner won; the repair path handles it next time

        // 5. Send
        var result = await sender.SendAsync(rendered.ToOutbound(lead, campaign, message, email.Value, app.Value, unsubscribeTokens), ct);

        // 6. Record, in ONE save: message → Sent AND enrollment advanced AND lead → Contacted
        if (result.Success)
        {
            message.Status = EmailMessageStatus.Sent; message.SentAt = clock.GetUtcNow().UtcDateTime; message.ProviderMessageId = result.ProviderMessageId;
            EnrollmentRules.AdvanceAfterSend(e, campaign.Steps.OrderBy(s => s.Order).ToList(), message.SentAt.Value, CampaignWindowFactory.For(campaign, ctx.WorkspaceTimeZone));
            e.SendAttempts = 0;
            if (LeadStatusRules.CanTransition(lead.Status, LeadStatus.Contacted)) lead.Status = LeadStatus.Contacted;
            await store.SaveAsync(ct);
            workspaceBudget[item.WorkspaceId] = left - 1;
            return SendOutcome.Sent;
        }
        return await HandleFailureAsync(ctx, message, result, now, ct);
    }
    // RepairAsync, HandleFailureAsync, BuildContentAsync, CompleteCampaignIfDoneAsync, AllowedByRecipientList: below
}
```
**Repair path (`RepairAsync`)** — what happens when a message for `(enrollment, step)` already exists:
| Existing message | Meaning | Action |
|---|---|---|
| `Sent`/`Delivered`/`Bounced`/`Dropped` | SendGrid accepted it; the process died before advancing the enrollment | **Advance** the enrollment (as if just sent), no re-send |
| `Queued`, newer than `StaleQueuedMinutes` | another runner is mid-send | skip this run |
| `Queued`, older than `StaleQueuedMinutes` | process died around the SendGrid call; delivery unknown | mark `Failed("Uncertain: …not retried to avoid a duplicate")`, **advance** the enrollment (step skipped for this lead), log a warning |
| `Failed` | previous terminal failure | enrollment already failed; if still `Active`, mark it `Failed` with the stored error |

**`HandleFailureAsync`**: retryable (`result.Retryable`) → `DeleteMessageAsync`, `e.SendAttempts++`; if `< MaxAttempts` set `e.NextSendAt = now + BackoffMinutes[attempt-1]` (still `Active`), else `Fail(e, "Provider unavailable after 3 attempts")`; non-retryable → `message.Status = Failed`, `message.Error = result.Error`, `Fail(e, …)`. Saved in one `SaveAsync`. `Fail(e, reason)` sets `Status = Failed`, `FailureReason`, `NextSendAt = null`.

**`BuildContentAsync`**
```csharp
var values = TemplateRenderer.ValuesFor(lead);
var subject = TemplateRenderer.Render(step.SubjectTemplate, values);
var body = TemplateRenderer.Render(step.BodyTemplate, values);
var missing = subject.MissingTokens.Concat(body.MissingTokens).Distinct().ToList();
var usedAi = false; var finalSubject = subject.Text; var finalBody = body.Text;

if (missing.Count == 0 && step.UseAiPersonalisation && await budget.CheckAsync(workspaceId, ct) == BudgetState.Ok)
{
    try
    {
        var composed = await composer.ComposeAsync(new EmailComposeContext(lead.Name, lead.Email, lead.Title,
            AdditionalContext: $"Personalise this email for the recipient. Keep its intent, call to action and under 120 words.\n---\nSubject: {subject.Text}\n\n{body.Text}\n---",
            lead.Company?.Name, lead.Company?.Industry, lead.Company?.Country), ct);
        await usage.RecordAsync(new AiUsageEntry(workspaceId, pricing.Model, composed.PromptTokens, composed.CompletionTokens, pricing.EstimateCostUsd(composed.PromptTokens, composed.CompletionTokens),
                                                 AiPurpose.Personalise, campaign.Id, e.Id), ct);
        finalSubject = composed.Subject; finalBody = composed.Body; usedAi = true;
    }
    catch (AiProviderException ex) { logger.LogWarning("AI personalisation failed ({Kind}); sending the template as-is.", ex.Kind); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { logger.LogWarning("AI personalisation timed out; sending the template as-is."); }
}
// footer is appended AFTER AI so the model can never drop or alter the unsubscribe link
var url = $"{app.PublicBaseUrl.TrimEnd('/')}/unsubscribe/{unsubscribeTokens.Create(workspaceId, lead.Id)}";
return new RenderedEmail(finalSubject.ReplaceLineBreaks(), UnsubscribeFooter.Append(finalBody, url), UnsubscribeFooter.AppendHtml(HtmlFromText(finalBody), url), usedAi, missing, url);
```
Sanitising the AI subject (single line, ≤ 300 chars) is applied the same way as template output. `ToOutbound` builds the `OutboundEmail`: `To`, `From` (`Campaign.FromName ?? EmailOptions.FromName`, config address), `ReplyTo = ReplyAddress.Encode(enrollmentId, email.InboundDomain)`, `TrackOpens = true`, `Headers = { "List-Unsubscribe": "<{PublicBaseUrl}/api/v1/unsubscribe/{token}>", "List-Unsubscribe-Post": "List-Unsubscribe=One-Click" }`, `CustomArgs = { "emailMessageId": id, "workspaceId": …, "campaignId": … }`, `Categories = ["campaign"]`.

**`CompleteCampaignIfDoneAsync`** — after the batch: if the campaign is `Active`, has ≥ 1 enrollment and `!CampaignHasLiveEnrollmentsAsync` → `Status = Completed`.

**`Sending/AiBudget.cs`** (23.3)
```csharp
public enum BudgetState { Ok, Warning, Exceeded }
public class AiBudgetOptions { public decimal MonthlyCostCapUsd { get; set; } = 20m; public decimal WarnAtFraction { get; set; } = 0.8m; }

public interface IAiBudget
{
    Task<BudgetState> CheckAsync(Guid workspaceId, CancellationToken ct);
    Task<AiBudgetStatus> GetStatusAsync(Guid workspaceId, CancellationToken ct);     // { SpentUsd, CapUsd, State, ResetsOn }
}
```
Implementation (`Infrastructure/Ai/EfAiBudget.cs`) sums `AiUsageLog.EstimatedCostUsd` for the workspace since the first of the UTC month (`ForWorkspace`-safe: takes `workspaceId`), compares to the cap. `ITokenUsageTracker.GetSummaryAsync` stays; `AiUsageEntry` gains optional trailing `Purpose = Compose, CampaignId = null, EnrollmentId = null`; `EfTokenUsageTracker` writes them. A **failed** provider call that was billed is recorded with `Succeeded = false` (Phase 1 behaviour preserved).
`ComposeEmailUseCase` (modified): call `budget.CheckAsync` first; `Exceeded` → throw `AiBudgetExceededException`. `AiController`: catch → `429 { code: "ai_budget_exceeded", message: "This workspace's monthly AI budget has been used. It resets on <date>." }`; `GET /ai/token-usage` response extended with `budget: { spentUsd, capUsd, state, resetsOn }`. The per-workspace **hard** ceiling remains the OpenAI dashboard cap; the global cap across workspaces is F29.7.

**Unsubscribe use case** — `UseCases/Sending/UnsubscribeUseCase.cs`
```csharp
public async Task<UnsubscribeResult> ExecuteAsync(string token, CancellationToken ct)
{
    if (!tokens.TryParse(token, out var workspaceId, out var leadId)) return UnsubscribeResult.Invalid;      // one generic 404 for any bad token
    var lead = await leads.GetForJobAsync(workspaceId, leadId, ct);                                          // ForWorkspace; includes soft-deleted
    if (lead is null) return UnsubscribeResult.Invalid;
    await suppression.AddAsync(workspaceId, lead.Email, SuppressionReason.Unsubscribed, lead.Id, ct);       // idempotent upsert
    if (LeadStatusRules.CanTransition(lead.Status, LeadStatus.Unsubscribed)) lead.Status = LeadStatus.Unsubscribed;
    await enrollments.StopAllForLeadAsync(workspaceId, lead.Id, StopEvent.Unsubscribed, ct);                // every campaign, via EnrollmentRules.ApplyStop
    await leads.SaveAsync(ct);
    return UnsubscribeResult.Done(Mask(lead.Email));
}
```
`Mask("jane.doe@acme.com")` → `j***@acme.com` (the landing page shows it; the token holder already got the email, but the page is public).

### Infrastructure

- `SenderStore`, `SuppressionList` (EF), `EfAiBudget`; DbSets for `EmailMessage`, `SuppressedEmail`; configs; migration **`AddSendingEngine`** (EmailMessages, SuppressedEmails, `SendAttempts` on enrollments, `AiUsageLogs` extra columns with defaults `Purpose = 0`, `Succeeded = 1`, nullable ids).
- **`Jobs/CampaignSenderJob.cs`**
```csharp
public class CampaignSenderJob(SendDueEmailsUseCase useCase, IOptions<SendingOptions> options, ILogger<CampaignSenderJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    [AutomaticRetry(Attempts = 0)]                       // the next minute's run is the retry
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        var s = await useCase.ExecuteAsync(ct);
        if (s.Total > 0) logger.LogInformation("Sender run: {Sent} sent, {Failed} failed, {Stopped} stopped, {Capped} capped, {Throttled} throttled", s.Sent, s.Failed, s.Stopped, s.CapReached, s.Throttled);
    }
}
```
- **`Program.cs`** — after `app.Build()`, when Hangfire is enabled: `RecurringJob.AddOrUpdate<CampaignSenderJob>("campaign-sender", j => j.ExecuteAsync(CancellationToken.None), Cron.Minutely);`. Register `ISenderStore`, `ISuppressionList`, `IAiBudget`, `SendDueEmailsUseCase`, `UnsubscribeUseCase`, `CampaignSenderJob`, `Configure<SendingOptions>`, `Configure<AiBudgetOptions>`. `appsettings.Development.json`: `"Sending": { "RecipientAllowList": [] }` with a comment in the README telling developers to fill it (e.g. their own address/domain) **before** enrolling anything real.
- **`Controllers/V1/UnsubscribeController.cs`** (new, `[AllowAnonymous]`, `api/v1/unsubscribe`, `[EnableRateLimiting(RateLimiting.UnsubscribePolicy)]` 30/min/IP): `GET {token}` → `{ email: masked, alreadyUnsubscribed }` (read-only), `POST {token}` → `204`; invalid token → `404` for both. `POST` ignores the body, so RFC 8058 one-click form posts work. Add both to the F19 anonymous allow-list.

### Angular

- **`features/public/unsubscribe/`** (public route `unsubscribe/:token`, declared in `AppModule`; no auth/shell): loads `GET`, shows "Unsubscribe **j\*\*\*@acme.com** from future emails?" with a button; success state "You've been unsubscribed. You won't receive more emails from us."; already-unsubscribed and invalid-link states; works without JS-side session (the interceptor must not try to refresh a session on a public page — `AuthInterceptor` already only attaches a token when present). Strips nothing from the URL (the token is in the path, and the page is harmless to reload).
- **AI budget banner:** `core/ai/ai-usage.service.ts` (`GET /api/v1/ai/token-usage` → `budget`), `core/layout/ai-budget-banner` shown in `Shell` when `state` is `Warning` ("80% of this month's AI budget used") or `Exceeded` ("AI drafts are paused until <date>"), refreshed on shell init and after each compose. `LeadDetail` "Generate draft": on `429` with `code: 'ai_budget_exceeded'` show the budget message and disable the button instead of the generic rate-limit text.

## Tests (priority — PBI 23.5)
All with `ISenderStore`/`ISuppressionList` fakes (or SQLite `TestDb` where noted), `FakeEmailSender` (programmable `SendResult`), `FakeEmailComposer`, `FakeTimeProvider`.
- **Happy path:** due enrollment → one message `Queued` then `Sent` with provider id; enrollment advanced (`NextStepOrder`, `NextSendAt` = F22 scheduler result), lead `Contacted`; last step → `Completed`; campaign `Completed` when the last live enrollment finishes; email contains the unsubscribe URL and the `List-Unsubscribe(-Post)` headers, `Reply-To` decodes back to the enrollment id (`ReplyAddress` round-trip), `TrackOpens` true, custom args present.
- **Time zones / DST / windows** (integration of F22 scheduling into the sender): a due enrollment whose follow-up falls on a spring-forward day gets the F22-computed `NextSendAt`; "due" uses UTC `now`; a message queued at 23:59 workspace-local counts toward *that* local day's cap, not the next (day boundary computed in the workspace zone — Dubai and New York cases).
- **Idempotency under retry/crash (documented behaviour):**
  1. crash **after** `TryInsertQueuedAsync`, **before** `SendAsync` (simulate by throwing from the fake sender, then re-run with a stale clock) → no second send, message `Failed("Uncertain…")`, enrollment advanced, warning logged;
  2. crash **after** `SendAsync`, before `SaveAsync` (fake store throws once on save, message left `Queued`) → next run does **not** call the sender again (assert call count 1);
  3. message already `Sent` but enrollment not advanced → repair advances without sending;
  4. two overlapping runs (same due list, run concurrently via `Task.WhenAll` on a SQLite-backed store) → exactly one send (unique `(EnrollmentId, StepId)`);
  5. unique index violation surfaces as `TryInsertQueuedAsync == false`, never an exception.
- **Provider failures:** retryable → message row deleted, `SendAttempts` 1→2→3 with backoff `NextSendAt` 5 m/30 m/2 h, third failure → enrollment `Failed`; non-retryable (e.g. 400) → immediate `Failed` with the error, **other enrollments in the batch still send**; an exception thrown for one enrollment is counted in `Errors` and doesn't stop the loop.
- **Caps:** campaign `DailySendCap` stops further sends that day (`CapReached`) and resets the next local day; `WorkspaceDailyCap` applies across campaigns; `MaxPerWorkspacePerRun` throttles one workspace without starving another (**cross-workspace fairness test**: 100 due in A, 3 in B → B's 3 all sent in the same run).
- **Suppression & stop conditions:** lead `Unsubscribed`/`Bounced` after enrollment → `ApplyStop`, no send; email in `SuppressedEmail` but lead status `New` → no send; `Replied` lead → enrollment `Replied`; invalid verification → `Failed`; allow-list blocks non-matching recipients and allows matching domain/address (case-insensitive); paused campaign's enrollments are never sent; enrollment of a soft-deleted lead is stopped.
- **Content:** missing token → `Failed` naming the token, others unaffected; fallback token used; **AI**: on → composer called, usage row recorded with `Purpose = Personalise`, `CampaignId`, `EnrollmentId`; AI throws (`AiProviderException`, timeout) → template sent unchanged and `UsedAi = false`; AI budget `Exceeded` → composer **not called**, template sent; footer present even if the model's output omits/alters links (footer appended after AI); AI subject containing a newline is flattened.
- **Unsubscribe:** valid token → lead `Unsubscribed`, suppression row, **all** enrollments in all campaigns stopped; second call idempotent; invalid/tampered/other-workspace-mismatched token → 404 body identical to unknown lead; `GET` never changes state; masks the email; works for a soft-deleted lead; ingestion/enrollment now report the address as `Suppressed`.
- **AI budget:** spend below/at/above cap boundaries (Warning at 80%, Exceeded at 100%); month rollover resets; another workspace's spend ignored; `ComposeEmailUseCase` throws `AiBudgetExceededException` and `AiController` returns `429 ai_budget_exceeded`; Phase 1 `EfTokenUsageTrackerTests` still green with the extended entry.
- **Authorization inventory (F19):** unsubscribe routes anonymous & allow-listed; nothing else.
- Angular: `unsubscribe.spec.ts` (GET on load with no POST; button posts; invalid token state), `ai-budget-banner.spec.ts` (states), `lead-detail.spec.ts` (budget message on `ai_budget_exceeded`).

## Not in this feature
Open/bounce/delivered events (F28 updates `EmailMessage` and `SuppressedEmail` from the Event Webhook), inbound replies (F25), the campaigns UI (F24), HTML templates/attachments, send-time jitter or randomised delays, per-campaign pause of a single enrollment, a manual "send now" button, an outbox for system email.

## Verification
1. `dotnet ef migrations add AddSendingEngine …`; `dotnet test`.
2. Set `Sending:RecipientAllowList` to your own address; `Email:Provider=SendGrid` (F17/F18 done); create a 3-step campaign with delays 0/0/0 in the API (shorten delays to minutes by editing `NextSendAt` in the DB for the demo), enroll 3 test leads using addresses you own, activate.
3. Within ~1 minute step 1 arrives in each inbox: footer with the unsubscribe link, `List-Unsubscribe` headers present (view source), Reply-To is `r-<guid>@reply.leads…`. Follow-ups fire on schedule. Click the unsubscribe link → landing page → confirm → no further emails; the lead shows `Unsubscribed`.
4. Kill the API during a send (breakpoint between insert and send) and restart → no duplicate email; the message shows `Failed("Uncertain…")`.
5. Watch `/hangfire` → recurring job `campaign-sender` every minute; AI cost rows visible in `AiUsageLogs` for a step with AI personalisation on; push the budget over the cap in config → the banner appears and drafts stop.
