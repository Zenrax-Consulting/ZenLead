# Feature 22 — Campaign Domain & Builder API

**Branch:** `feature/campaigns-backend`
**Sprint:** 2 (may slip to the start of Sprint 3 — see the parent plan's Sprint 2 note)
**Depends on:** F11 (tenant filter, `LeadStatusRules`), F14.7 (`EnrollmentEligibility`), F18 (`Workspace.TimeZone`, `TimeZones`, `EmailOptions`). The fake `IEmailSender` is enough — **nothing in this feature sends mail**.

## Goal
The campaign model and its API: campaigns with ordered steps, enrollments of leads, the **pure scheduling logic** (`NextSendAt` across time zones, send windows and DST; step advancement; stop conditions), template rendering with an unsubscribe footer, activation validation, and the edit rules for running campaigns. F23 (sender) and F24 (UI) are built on this; the scheduling code is the most heavily tested part of Phase 2.

## Design decisions
- **Time zone = the workspace's** (`Workspace.TimeZone`, F18). `Campaign.TimeZoneId` is an optional override, null by default.
- **Sender identity** is the single configured sender (F17.3): the parent plan's `FromSenderId` is replaced by an optional `Campaign.FromName`; the address is `EmailOptions.FromAddress`.
- **Delays count calendar days in the workspace zone**, not 24-hour blocks, so "3 days later at 10:00" stays 10:00 across a DST change. The result is then clamped into the send window (allowed weekdays + daily time range).
- **Enrollment progress is one number**: `NextStepOrder` (1-based order of the next step to send; `steps+1` once completed). "Has any enrollment passed step *k*?" is simply `NextStepOrder > k` — that is what the running-campaign edit rules use (no dependency on F23's `EmailMessage`).
- **Pause does not rewrite enrollments.** The sender (F23) only picks enrollments of `Active` campaigns, so `NextSendAt` values simply age while paused; on resume, overdue enrollments are due immediately and the daily/per-minute caps spread them out.
- **Draft campaigns hold enrollments with `NextSendAt = null`**; activation stamps them. Enrolling into an already-active campaign stamps immediately.
- **Tokens:** `{{firstName}}`, `{{lastName}}`, `{{name}}`, `{{company}}`, `{{title}}` (parent plan names the first three that matter; the others are free). Optional fallback `{{firstName|there}}`. A token with no value and no fallback **blocks the lead** at enrollment (and again at send time in F23) rather than sending "Hi ,".

## Files to add/modify

### Domain — entities & enums

**`ZenLead.Domain/Enums/`** (new): `CampaignStatus { Draft, Active, Paused, Completed }`, `EnrollmentStatus { Active, Paused, Completed, Replied, Unsubscribed, Bounced, Failed }`, `[Flags] SendDays { None=0, Mon=1, Tue=2, Wed=4, Thu=8, Fri=16, Sat=32, Sun=64, Weekdays=31, All=127 }`.

**`ZenLead.Domain/Entities/Campaign.cs`**, **`CampaignStep.cs`**, **`CampaignEnrollment.cs`** (new; all `ITenantEntity`)
```csharp
public class Campaign : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public string? FromName { get; set; }                    // null → EmailOptions.FromName
    public int DailySendCap { get; set; } = 30;              // 1..500; F23 also applies a workspace warm-up cap
    public TimeOnly WindowStart { get; set; } = new(9, 0);   // local time in the workspace/campaign zone
    public TimeOnly WindowEnd { get; set; } = new(17, 0);
    public SendDays SendDays { get; set; } = SendDays.Weekdays;
    public string? TimeZoneId { get; set; }                  // optional override of Workspace.TimeZone
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public List<CampaignStep> Steps { get; set; } = [];
}

public class CampaignStep : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CampaignId { get; set; }
    public int Order { get; set; }                           // 1-based, contiguous
    public int DelayDays { get; set; }                       // days after the PREVIOUS step was sent; step 1 must be 0
    public string SubjectTemplate { get; set; } = string.Empty;
    public string BodyTemplate { get; set; } = string.Empty;
    public bool UseAiPersonalisation { get; set; }
}

public class CampaignEnrollment : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid LeadId { get; set; }
    public int NextStepOrder { get; set; } = 1;
    public DateTime? NextSendAt { get; set; }                // UTC; null in Draft campaigns and once finished
    public EnrollmentStatus Status { get; set; } = EnrollmentStatus.Active;
    public DateTime EnrolledAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? FailureReason { get; set; }
    public Lead? Lead { get; set; }
}
```
**EF configs** (`EntityConfigurations.cs`): `Campaign` (`Name` 200, `TimeZoneId` 64, `FromName` 100, index `(WorkspaceId, Status)`); `CampaignStep` (`SubjectTemplate` 300, `BodyTemplate` `nvarchar(max)`, **unique `(CampaignId, Order)`**, FK Campaign cascade); `CampaignEnrollment` (**unique `(CampaignId, LeadId)`**, index `(Status, NextSendAt)` for the sender query, index `(WorkspaceId, LeadId)`, `FailureReason` 500, FK Campaign cascade, FK Lead **Restrict** — soft-deleted leads keep their history). `DbSet`s on the context. Migration **`AddCampaigns`**.

### Domain — scheduling (pure; the heavily tested part)

**`ZenLead.Domain/Scheduling/SendWindow.cs`** (new)
```csharp
public record SendWindow(TimeZoneInfo Zone, TimeOnly Start, TimeOnly End, SendDays Days)
{
    public bool IsValid => Start < End && Days != SendDays.None;
    public bool Allows(DayOfWeek day) => (Days & ToFlag(day)) != 0;
    private static SendDays ToFlag(DayOfWeek d) => (SendDays)(1 << (((int)d + 6) % 7));   // Mon=1 … Sun=64
}
```

**`ZenLead.Domain/Scheduling/SendScheduler.cs`** (new)
```csharp
public static class SendScheduler
{
    private const int MaxLookaheadDays = 14;

    /// <summary>Earliest UTC instant ≥ (after + delayDays calendar days, in the window's zone) that falls inside the send window.</summary>
    public static DateTime NextSendAt(DateTime afterUtc, int delayDays, SendWindow window)
    {
        if (!window.IsValid) throw new ArgumentException("Send window is invalid.", nameof(window));
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), window.Zone);
        var candidate = DateTime.SpecifyKind(local.AddDays(delayDays), DateTimeKind.Unspecified);    // calendar days → DST-safe

        for (var i = 0; i <= MaxLookaheadDays; i++)
        {
            var day = candidate.Date;
            var start = day + window.Start.ToTimeSpan();
            var end = day + window.End.ToTimeSpan();

            if (!window.Allows(day.DayOfWeek)) candidate = day.AddDays(1) + window.Start.ToTimeSpan();
            else if (candidate < start) candidate = start;
            else if (candidate >= end) candidate = day.AddDays(1) + window.Start.ToTimeSpan();   // End is exclusive
            else return ToUtc(candidate, window.Zone);
        }
        throw new InvalidOperationException("No send slot found within the lookahead (window has no allowed days?).");
    }

    /// <summary>Local → UTC with DST edge cases resolved deterministically.</summary>
    internal static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))                       // spring-forward gap: that wall-clock time never happens → first valid moment after it
            local = local.AddHours(1);
        if (zone.IsAmbiguousTime(local))                     // fall-back overlap: take the FIRST occurrence (larger UTC offset)
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
```
> After a "gap" shift the candidate may land past `End` on a tiny window; acceptable (still valid UTC, sent at the next poll). A test pins the behaviour.

**`ZenLead.Domain/Scheduling/EnrollmentRules.cs`** (new)
```csharp
public static class EnrollmentRules
{
    /// <summary>After step <paramref name="sentStepOrder"/> was sent at <paramref name="sentAtUtc"/>: schedule the next step or complete.</summary>
    public static void AdvanceAfterSend(CampaignEnrollment e, IReadOnlyList<CampaignStep> steps, DateTime sentAtUtc, SendWindow window)
    {
        e.LastSentAt = sentAtUtc;
        var next = steps.Where(s => s.Order > e.NextStepOrder).OrderBy(s => s.Order).FirstOrDefault();
        if (next is null) { e.NextStepOrder = steps.Max(s => s.Order) + 1; e.NextSendAt = null; e.Status = EnrollmentStatus.Completed; return; }
        e.NextStepOrder = next.Order;
        e.NextSendAt = SendScheduler.NextSendAt(sentAtUtc, next.DelayDays, window);
    }

    public enum StopEvent { Replied, Unsubscribed, Bounced }

    /// <summary>Stop conditions only act on live enrollments; finished ones keep their final status.</summary>
    public static bool ApplyStop(CampaignEnrollment e, StopEvent stop)
    {
        if (e.Status is not (EnrollmentStatus.Active or EnrollmentStatus.Paused)) return false;
        e.Status = stop switch { StopEvent.Replied => EnrollmentStatus.Replied, StopEvent.Unsubscribed => EnrollmentStatus.Unsubscribed, _ => EnrollmentStatus.Bounced };
        e.NextSendAt = null;
        return true;
    }

    public static bool IsDue(CampaignEnrollment e, DateTime nowUtc) => e.Status == EnrollmentStatus.Active && e.NextSendAt is { } at && at <= nowUtc;
}
```
`AdvanceAfterSend` is what F23 calls inside the same transaction that marks the message sent (§1 idempotency rule).

### Domain — templates

**`ZenLead.Domain/Templates/TemplateRenderer.cs`** (new)
```csharp
public record RenderedTemplate(string Text, IReadOnlyList<string> MissingTokens);

public static class TemplateRenderer
{
    public static readonly IReadOnlySet<string> KnownTokens =
        new HashSet<string>(["firstName", "lastName", "name", "company", "title"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TokenPattern = new(@"\{\{\s*(?<name>[A-Za-z]+)\s*(\|(?<fallback>[^}]*))?\}\}", RegexOptions.Compiled);

    public static IReadOnlyList<string> UnknownTokens(string template)
        => TokenPattern.Matches(template).Select(m => m.Groups["name"].Value).Where(n => !KnownTokens.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> RequiredTokens(string template)       // tokens with no fallback
        => TokenPattern.Matches(template).Where(m => !m.Groups["fallback"].Success).Select(m => m.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static RenderedTemplate Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var missing = new List<string>();
        var text = TokenPattern.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            values.TryGetValue(name, out var value);
            if (!string.IsNullOrWhiteSpace(value)) return Sanitize(value);
            if (m.Groups["fallback"].Success) return Sanitize(m.Groups["fallback"].Value.Trim());
            missing.Add(name);
            return string.Empty;
        });
        return new RenderedTemplate(text, missing);
    }

    /// <summary>Lead data is untrusted (CSV/provider). No CR/LF/control chars → no header injection through subjects, no layout games in bodies.</summary>
    private static string Sanitize(string v) => Regex.Replace(v.Trim(), @"[\u0000-\u001F\u007F]+", " ");

    /// <summary>Token values for a lead; first/last name split on the first space.</summary>
    public static Dictionary<string, string?> ValuesFor(Lead lead)
    {
        var parts = lead.Name.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["firstName"] = parts.ElementAtOrDefault(0), ["lastName"] = parts.ElementAtOrDefault(1), ["name"] = lead.Name,
            ["company"] = lead.Company?.Name, ["title"] = lead.Title
        };
    }
}
```
**`ZenLead.Domain/Templates/UnsubscribeFooter.cs`** (new) — `Append(string body, string url)` adds `"\n\n—\nDon't want to hear from us? Unsubscribe: {url}"`; `AppendHtml` adds the twin (URL HTML-encoded). Required on every campaign email (deliverability/compliance); the builder UI shows it as a locked footer in preview (F24).

### Application

**`Abstractions/IUnsubscribeTokens.cs`** (new) + **`ZenLead.Infrastructure/Security/HmacUnsubscribeTokens.cs`**
```csharp
public interface IUnsubscribeTokens
{
    string Create(Guid workspaceId, Guid leadId);
    bool TryParse(string token, out Guid workspaceId, out Guid leadId);      // constant-time MAC check; false on any tampering/format error
}
```
Token = `base64url("{workspaceId:N}.{leadId:N}")` + `.` + `base64url(HMAC-SHA256(payload, key))`, key from **`Unsubscribe:SigningKey`** (new required secret, ≥ 32 bytes; add to `StartupConfiguration.RequiredKeys`, README, and the `ApiFactory` config). Tokens **do not expire** (an unsubscribe link must keep working). Rotating the key breaks old links, so it is not rotated casually (noted in F30's runbook). The public endpoint that consumes it is F23.4.

**`Dtos/Campaigns/CampaignDtos.cs`** (new)
```csharp
public record CampaignRequest(string Name, string? FromName, int DailySendCap, string WindowStart, string WindowEnd, SendDays SendDays, string? TimeZoneId);   // "09:00"
public record StepRequest(int DelayDays, string SubjectTemplate, string BodyTemplate, bool UseAiPersonalisation);
public record ReorderStepsRequest(IReadOnlyList<Guid> OrderedStepIds);
public record EnrollRequest(IReadOnlyList<Guid>? LeadIds, LeadQuery? Filter, bool DryRun = false);

public record StepResponse(Guid Id, int Order, int DelayDays, string SubjectTemplate, string BodyTemplate, bool UseAiPersonalisation);
public record CampaignResponse(Guid Id, string Name, CampaignStatus Status, string? FromName, int DailySendCap, string WindowStart, string WindowEnd,
                               SendDays SendDays, string EffectiveTimeZone, DateTime CreatedAt, DateTime? ActivatedAt, IReadOnlyList<StepResponse> Steps, EnrollmentCounts Counts);
public record EnrollmentCounts(int Total, int Active, int Completed, int Replied, int Unsubscribed, int Bounced, int Failed);
public record CampaignListItem(Guid Id, string Name, CampaignStatus Status, int StepCount, EnrollmentCounts Counts, DateTime CreatedAt);

public record EnrollResult(int Enrolled, int AlreadyEnrolled, int SkippedSuppressed, int SkippedInvalidEmail, int SkippedMissingTokens,
                           int NotFound, int WarnUnverified, int WarnRisky, bool Truncated);
public record ActivationProblem(string Code, string Message);                    // e.g. no_steps, bad_token, empty_subject, domain_unverified, window_invalid
public record EnrollmentRow(Guid Id, Guid LeadId, string LeadName, string LeadEmail, int NextStepOrder, DateTime? NextSendAt, EnrollmentStatus Status, string? FailureReason);
```
**Validators** (`Validation/Campaigns/`): `CampaignRequestValidator` (name 1–200; cap 1–500; window times parse as `HH:mm`, start < end; `SendDays != None`; `TimeZoneId` null or `TimeZones.TryFind`; `FromName` ≤ 100, no CR/LF); `StepRequestValidator` (`DelayDays` 0–90, subject 1–300 and no CR/LF, body 1–10,000, **no unknown tokens** via `TemplateRenderer.UnknownTokens`); `EnrollRequestValidator` (exactly one of `LeadIds` (≤ 5,000) or `Filter`).

**Repositories** (Application interfaces, EF impls in `Infrastructure/Persistence`): `ICampaignRepository` (`AddAsync`, `GetWithStepsAsync(id)` tracked, `ListWithCountsAsync()` — one grouped query, no N+1 —, `UpdateAsync`, `AddStepAsync`, `RemoveStepAsync`, `SaveAsync`), `ICampaignEnrollmentRepository` (`ExistingLeadIdsAsync(campaignId, leadIds)`, `AddRangeAsync`, `MaxNextStepOrderAsync(campaignId)`, `StampNextSendAtAsync(campaignId, window, now)` — loads `Status=Active && NextSendAt==null` enrollments in pages of 1000 and sets `NextSendAt = SendScheduler.NextSendAt(now, 0, window)`, `ListAsync(campaignId, status?, page, pageSize)` with `Lead` included, `CountsAsync(campaignId)`). `ILeadRepository.GetByIdsAsync` (F14) is reused; `ListIdsAsync` (F13) for the filter case.

**`UseCases/Campaigns/CampaignWindowFactory.cs`** — builds the `SendWindow` from a campaign + the workspace zone (`Workspace.TimeZone`, overridden by `Campaign.TimeZoneId`; falls back to UTC if an old id no longer resolves, logging a warning):
```csharp
public static SendWindow For(Campaign c, string workspaceTimeZone)
{
    TimeZones.TryFind(c.TimeZoneId ?? workspaceTimeZone, out var zone);       // zone = UTC when not found
    return new SendWindow(zone, c.WindowStart, c.WindowEnd, c.SendDays);
}
```

**`UseCases/Campaigns/EnrollLeadsUseCase.cs`** (new)
```csharp
public async Task<EnrollOutcome> ExecuteAsync(Guid workspaceId, Guid campaignId, EnrollRequest request, CancellationToken ct)
{
    var campaign = await campaigns.GetWithStepsAsync(campaignId, ct);
    if (campaign is null || campaign.WorkspaceId != workspaceId) return EnrollOutcome.NotFound;
    if (campaign.Status == CampaignStatus.Completed) return EnrollOutcome.CampaignCompleted;

    const int MaxPerRequest = 5000;
    var ids = request.LeadIds ?? await leads.ListIdsAsync(workspaceId, request.Filter!, MaxPerRequest + 1, ct);
    var truncated = ids.Count > MaxPerRequest; ids = ids.Distinct().Take(MaxPerRequest).ToList();

    var found = await leads.GetByIdsAsync(workspaceId, ids, ct);                 // other tenants' / deleted ids simply don't resolve
    var already = await enrollments.ExistingLeadIdsAsync(campaignId, found.Select(l => l.Id).ToList(), ct);
    var requiredTokens = campaign.Steps.SelectMany(s => TemplateRenderer.RequiredTokens(s.SubjectTemplate).Concat(TemplateRenderer.RequiredTokens(s.BodyTemplate)))
                                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    var counts = new EnrollCounters { NotFound = ids.Count - found.Count };
    var toAdd = new List<CampaignEnrollment>();
    var window = CampaignWindowFactory.For(campaign, await workspaces.GetTimeZoneAsync(workspaceId, ct));
    var now = clock.GetUtcNow().UtcDateTime;
    var nextSendAt = campaign.Status == CampaignStatus.Draft ? (DateTime?)null : SendScheduler.NextSendAt(now, 0, window);

    foreach (var lead in found)
    {
        if (already.Contains(lead.Id)) { counts.AlreadyEnrolled++; continue; }
        var eligibility = EnrollmentEligibility.Check(lead.Status, lead.EmailVerificationStatus);
        if (eligibility.Kind == EligibilityKind.Blocked)
        { if (lead.EmailVerificationStatus == EmailVerificationStatus.Invalid) counts.SkippedInvalidEmail++; else counts.SkippedSuppressed++; continue; }
        var values = TemplateRenderer.ValuesFor(lead);
        if (requiredTokens.Any(t => string.IsNullOrWhiteSpace(values.GetValueOrDefault(t)))) { counts.SkippedMissingTokens++; continue; }
        if (eligibility.Kind == EligibilityKind.EligibleWithWarning) { if (lead.EmailVerificationStatus == EmailVerificationStatus.Risky) counts.WarnRisky++; else counts.WarnUnverified++; }

        counts.Enrolled++;
        toAdd.Add(new CampaignEnrollment { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaignId, LeadId = lead.Id,
                                           NextStepOrder = 1, NextSendAt = nextSendAt, Status = EnrollmentStatus.Active, EnrolledAt = now });
    }
    if (!request.DryRun && toAdd.Count > 0) await enrollments.AddRangeAsync(toAdd, ct);    // unique (CampaignId, LeadId) is the backstop against a double click
    return EnrollOutcome.Ok(counts.ToResult(truncated));
}
```
`DryRun` runs the identical code but writes nothing, so the F24 confirm dialog's numbers are exactly what the real call will do. A unique-index violation on `AddRangeAsync` (concurrent double submit) is caught and retried once after re-reading `ExistingLeadIdsAsync`.

**`UseCases/Campaigns/ActivateCampaignUseCase.cs`** (new) — validation (`22.4`) and state change
```csharp
public static IReadOnlyList<ActivationProblem> Validate(Campaign c, EmailOptions email)
{
    var problems = new List<ActivationProblem>();
    if (c.Steps.Count == 0) problems.Add(new("no_steps", "Add at least one step."));
    var ordered = c.Steps.OrderBy(s => s.Order).ToList();
    if (ordered.Select((s, i) => s.Order == i + 1).Any(ok => !ok)) problems.Add(new("steps_unordered", "Step order must be 1, 2, 3…"));
    if (ordered.FirstOrDefault() is { DelayDays: not 0 }) problems.Add(new("first_delay", "The first step must send immediately (delay 0)."));
    foreach (var s in ordered)
    {
        if (string.IsNullOrWhiteSpace(s.SubjectTemplate)) problems.Add(new("empty_subject", $"Step {s.Order} has no subject."));
        if (string.IsNullOrWhiteSpace(s.BodyTemplate)) problems.Add(new("empty_body", $"Step {s.Order} has no body."));
        foreach (var t in TemplateRenderer.UnknownTokens(s.SubjectTemplate).Concat(TemplateRenderer.UnknownTokens(s.BodyTemplate)))
            problems.Add(new("bad_token", $"Step {s.Order} uses unknown token {{{{{t}}}}}."));
    }
    if (c.WindowStart >= c.WindowEnd || c.SendDays == SendDays.None) problems.Add(new("window_invalid", "Send window is invalid."));
    if (!email.DomainVerified) problems.Add(new("domain_unverified", "The sending domain isn't verified yet."));
    return problems;
}
```
`ExecuteAsync`: only `Draft`/`Paused` can activate (`Active` → no-op 200, `Completed` → 409); problems → 409 with the list; on success `Status = Active`, `ActivatedAt ??= now`, then `StampNextSendAtAsync` for enrollments that have `NextSendAt == null`. `PauseCampaignUseCase`: `Active → Paused` only. Completion (`Active → Completed` when no live enrollments remain and ≥ 1 enrollment) is done by F23's sender when it finishes the last enrollment — F22 only defines `CampaignStatus.Completed` and the rule.

**`UseCases/Campaigns/CampaignStepsUseCase.cs`** (new) — add/update/delete/reorder with the running-campaign rules:
- **Add**: appended with `Order = max+1`; `DelayDays` validated; on Active/Paused it extends the plan for enrollments still in progress (finished ones stay finished — documented).
- **Update** (subject/body/AI flag/delay): always allowed. A changed `DelayDays` only affects enrollments that haven't yet been scheduled into that step (their `NextSendAt` for it is computed when the previous step is sent). The step *currently* scheduled keeps its `NextSendAt`.
- **Delete / Reorder**: refused with 409 `{code:"step_in_use"}` when `await enrollments.MaxNextStepOrderAsync(campaignId) > step.Order` (for reorder: `> 1`) — i.e. some enrollment has already passed the affected step. In Draft (no sends) everything is free. After a delete, orders are re-numbered contiguous inside the same transaction; deleting step 1 re-zeroes the new first step's `DelayDays`.
- Rules are stated in the API error messages and surfaced in the F24 UI.

### Api

**`CampaignsController`** (new, `[Authorize]`, `api/v1/campaigns`) — thin; every action starts with the workspace-claim check and treats a campaign from another workspace as `404`:
| Route | Behaviour |
|---|---|
| `GET /` | `CampaignListItem[]` (grouped counts, newest first) |
| `POST /` | create `Draft`, `CreatedBy = this.UserId()`; 201 |
| `GET /{id}` | `CampaignResponse` with steps + counts |
| `PUT /{id}` | name/from/cap/window/days/zone; allowed in any non-`Completed` status (window changes apply to *future* scheduling only) |
| `POST /{id}/steps` · `PUT /{id}/steps/{stepId}` · `DELETE …` · `POST /{id}/steps/reorder` | `CampaignStepsUseCase` |
| `POST /{id}/enroll` | `EnrollLeadsUseCase`; `200 EnrollResult` (also for `dryRun`) |
| `GET /{id}/enrollments?status=&page=&pageSize=` | `PagedResult<EnrollmentRow>` |
| `POST /{id}/activate` · `POST /{id}/pause` | `ActivateCampaignUseCase` / `PauseCampaignUseCase`; problems → `409 { problems: [...] }` |
| `POST /{id}/preview` | body `{ leadId, stepId }` → `{ subject, body, missingTokens }` rendered with the footer placeholder; powers F24's "Preview with lead" without duplicating the renderer in TypeScript |

Register everything in `Program.cs` (`IUnsubscribeTokens`, repositories, use cases, validators via the existing assembly scan). `/api/v1/campaigns` needs no proxy change.

## Tests (priority)
**`Domain/Scheduling/SendSchedulerTests.cs`** (use IANA ids; **verify** they resolve on the Windows dev box — see F18's note)
- Inside window → returned unchanged (converted to UTC); before start → same day at start; exactly `End` → next allowed day at start (End exclusive); exactly `Start` → allowed.
- Weekend: Friday 17:00 with `Weekdays` and delay 0 → Monday 09:00 local; delay 3 from Thursday → Sunday → Monday 09:00; `SendDays` Tue/Thu only.
- **DST (America/New_York 2026):** spring-forward 2026-03-08 — a step due at 02:30 local (non-existent) resolves to 03:30 local = 07:30 UTC (gap shift); a 3-day delay from Friday 2026-03-06 10:00 EST lands **10:00 EDT** on Monday 2026-03-09 (= 14:00 UTC, not 15:00) proving calendar-day arithmetic; fall-back 2026-11-01 — 01:30 local (ambiguous) resolves to the **first** occurrence (05:30 UTC); delay across fall-back keeps local time-of-day.
- **Europe/London** (BST/GMT switch 2026-03-29 and 2026-10-25), **Asia/Dubai** (no DST, UTC+4), **Asia/Kolkata** (+5:30 half-hour offset), **Pacific/Auckland** (southern-hemisphere DST), a zone whose local date differs from UTC's date (late-evening UTC → next local day).
- Invalid window (start ≥ end, no days) throws; one allowed weekday with delay spanning 2 weeks still finds a slot; pathological input throws rather than looping forever.
- Table-driven property check: for 500 random instants × zones × windows, the result is ≥ the input instant, falls inside the window when converted back to local (allowing the documented gap-shift case), and `NextSendAt(result, 0, w) == result` (idempotent).

**`Domain/Scheduling/EnrollmentRulesTests.cs`** — advance through steps 1→2→3 (`NextStepOrder`, `NextSendAt` per delay), completion after the last (`Status=Completed`, `NextStepOrder = last+1`, `NextSendAt=null`); steps with gaps in `Order` after edits still advance to the next greater order; `ApplyStop` for Reply/Unsubscribe/Bounce from Active and Paused, **no change** from Completed/Replied/Failed; `IsDue` only for `Active` + past `NextSendAt`.

**`Domain/Templates/TemplateRendererTests.cs`** — all tokens; case-insensitive names; fallback used when blank; missing without fallback reported and replaced by empty; unknown tokens listed by `UnknownTokens`; `RequiredTokens` excludes fallbacked ones; **sanitising**: lead name `"Bob\r\nBcc: x@y.com"` renders on one line (subject-injection test); first/last split of one-word and multi-word names; `{{ firstName }}` with spaces; unmatched braces left alone; footer appended exactly once in text and HTML (URL encoded).

**`Application/Campaigns/EnrollLeadsUseCaseTests.cs`** (fakes + `FakeTimeProvider`) — skips `Unsubscribed`/`Bounced` and provider-`Invalid`; already-enrolled counted, not duplicated (re-posting the same ids enrolls 0); missing required token skipped, fallbacked token enrolled; `Unverified`/`Risky` enrolled **and** counted as warnings; `DryRun` returns identical counts and writes nothing; Draft campaign → `NextSendAt` null, Active campaign → stamped inside the window; `Completed` campaign refused; filter variant respects the 5,000 cap (`Truncated`); **cross-workspace**: another workspace's lead ids resolve to `NotFound` counts, another workspace's campaign → `NotFound`.

**`Application/Campaigns/ActivateCampaignUseCaseTests.cs`** — each problem code in isolation; unverified domain blocks; success stamps only enrollments with null `NextSendAt`; pause/resume transitions (`Active→Paused→Active`; `Draft→Paused` illegal; `Completed` can't activate).

**`Application/Campaigns/CampaignStepsUseCaseTests.cs`** — add appends contiguous order; delete/reorder allowed in Draft, refused once an enrollment is past the step (`MaxNextStepOrder`), allowed for an untouched later step; delete renumbers and re-zeroes the new first step's delay; update of a past step's text allowed; `DelayDays` edit doesn't change an already-stamped `NextSendAt`.

**`Infrastructure/Persistence/CampaignPersistenceTests.cs`** (`TestDb`) — unique `(CampaignId, LeadId)` and `(CampaignId, Order)` enforced; **tenant isolation** for campaigns/steps/enrollments (extend F11's reflection test — it now covers three more entities automatically); `ListWithCountsAsync` returns correct grouped counts in a single query (assert SQL count via a command interceptor if cheap); sender-style query `Status=Active AND NextSendAt<=now` uses the `(Status, NextSendAt)` index (smoke only).

**`Api/CampaignsControllerTests.cs`** — workspace B gets 404 on A's campaign for every verb (GET/PUT/steps/enroll/activate/pause/preview); activation problems come back as `409` with the list; `preview` returns `missingTokens`; validation errors for unknown tokens / bad window / bad zone; **`HmacUnsubscribeTokens`** tests: round-trip, tampered payload/MAC rejected, wrong key rejected, malformed strings never throw.

## Not in this feature
Sending (F23), the sender job and caps, unsubscribe endpoint/page (F23.4), campaign UI (F24), un-enrol/remove-from-campaign, cloning a campaign, A/B or branching, deleting campaigns, per-step send windows.

## Verification
`dotnet ef migrations add AddCampaigns …`, `dotnet test`. Then via `ZenLead.Api.http`: create a campaign (workspace zone `Asia/Dubai`), add 3 steps (delays 0/3/7) with `{{firstName|there}}`, preview for a lead, enroll 5 leads (one `Unsubscribed`, one with no company when `{{company}}` is used) with `dryRun:true` then for real, check counts and that `NextSendAt` is null; activate (409 until `Email:DomainVerified=true`), re-check `NextSendAt` values fall inside 09:00–17:00 Dubai on weekdays; edit step 2's body while Active (ok) and try to delete step 1 after bumping an enrollment's `NextStepOrder` in the DB (409).
