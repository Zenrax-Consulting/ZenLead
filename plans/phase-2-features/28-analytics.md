# Feature 28 — SendGrid Event Webhook & Analytics

**Branch:** `feature/analytics`
**Sprint:** 5 (the webhook half can start in Sprint 4 once F23 is sending real mail — see parent risk table)
**Depends on:** F23 (`EmailMessage`, `SuppressedEmail`, `emailMessageId`/`workspaceId` custom args), F25 (`InboxMessage` for reply counts), F22 (`EnrollmentRules.ApplyStop`), F11 (`ForWorkspace`, `LeadStatusRules`), the dev tunnel / production URL.

## Goal
Close the loop on what happened to each email: a **signed** Event Webhook that updates `EmailMessage` (delivered, opened, bounced, dropped, spam-reported) idempotently and out-of-order-safe, applies the bounce/spam suppression rules, plus workspace and per-campaign analytics (API + UI) that report opens honestly as approximate.

## Design decisions
- **Verify before touching anything.** SendGrid's signed webhook sends `X-Twilio-Email-Event-Webhook-Signature` (base64 ECDSA over **timestamp bytes + raw body bytes**, P-256/SHA-256) and `X-Twilio-Email-Event-Webhook-Timestamp`. The public key comes from the SendGrid UI. We verify on the **raw request bytes** (not re-serialised JSON) and return `401` on failure. **Verify** the exact header names and signing input against SendGrid's current "Event Webhook Security" docs when implementing.
- **Replay handling = idempotency, not a tight clock window.** SendGrid retries failed deliveries for up to 24 h with the original signature, so a short timestamp window would silently drop legitimate retries. We reject timestamps older than `EventWebhook:MaxAgeHours` (default 26) or in the future (> 5 min), and make every event idempotent on `sg_event_id`, so a replayed body changes nothing.
- **State only moves forward** (`Queued < Sent < Delivered`); `Bounced`/`Dropped` are terminal and may follow `Delivered` (asynchronous bounces exist). `open` implies delivery. Events for unknown messages are logged and acknowledged.
- **Correlation by our own id**: the `emailMessageId` custom arg (top-level in each event). The `workspaceId` arg must equal the message's workspace or the event is ignored — defence in depth even though the signature already authenticates the sender.
- **Analytics are cohort-based**: every figure is about messages *sent in the selected range* and what has happened to them since, so rates can't exceed 100 % and numbers don't shift as the range end moves. Denominators are explicit in the response.
- **Opens are approximate** (Apple Mail Privacy Protection pre-fetches pixels; some clients block them). The API flags it; the UI says so wherever an open number or rate appears.
- **Charts:** no charting dependency. Two small SVG components (a multi-series trend line and a horizontal bar list) with a title/description, direct labels, a "view as table" toggle and CSS-variable colours that work in light and dark. Apply the project's `dataviz` guidance (validated palette, redundant encodings, accessible fallbacks). Revisit a library only if more chart types arrive.

## Files to add/modify

### Domain

**`Entities/ProcessedWebhookEvent.cs`** (new; not tenant-scoped — events arrive before the workspace is trusted): `string SgEventId (PK, 100)`, `DateTime ReceivedAt`. A Hangfire housekeeping line (F29) deletes rows older than 30 days. Migration **`AddWebhookEventsAndAnalyticsIndexes`** also adds: `EmailMessage` index `(WorkspaceId, CampaignId, StepId, SentAt)`; `InboxMessage` index `(WorkspaceId, Direction, IsAutoReply, OccurredAt)`.

**`Sending/EmailEventRules.cs`** (new, pure)
```csharp
public enum EmailEventType { Delivered, Open, Bounce, Blocked, Dropped, SpamReport, Unsubscribe, Ignored }

public record EmailEvent(EmailEventType Type, DateTime OccurredAtUtc, string? Reason, string? Status, string? SmtpId);

public static class EmailEventRules
{
    /// <summary>Applies one event to a message. Returns what else should happen as a result.</summary>
    public static EventEffect Apply(EmailMessage m, EmailEvent e)
    {
        switch (e.Type)
        {
            case EmailEventType.Delivered:
                m.DeliveredAt ??= e.OccurredAtUtc;
                if (m.Status is EmailMessageStatus.Queued or EmailMessageStatus.Sent) m.Status = EmailMessageStatus.Delivered;   // never downgrade Bounced/Dropped
                break;
            case EmailEventType.Open:
                m.OpenedAt ??= e.OccurredAtUtc;                                                                                   // first open only: "unique opens"
                m.DeliveredAt ??= e.OccurredAtUtc;                                                                                // an open proves delivery even if the delivered event is late/lost
                if (m.Status is EmailMessageStatus.Queued or EmailMessageStatus.Sent) m.Status = EmailMessageStatus.Delivered;
                break;
            case EmailEventType.Bounce:
                m.BouncedAt ??= e.OccurredAtUtc; m.Status = EmailMessageStatus.Bounced; m.Error = Truncate(e.Reason, 500);
                return IsHard(e) ? EventEffect.HardBounce : EventEffect.SoftStop;
            case EmailEventType.Blocked:                                        // recipient server refused (reputation/policy): the address may be fine
                m.BouncedAt ??= e.OccurredAtUtc; m.Status = EmailMessageStatus.Bounced; m.Error = Truncate(e.Reason, 500);
                return EventEffect.SoftStop;
            case EmailEventType.Dropped:                                        // SendGrid refused to send
                m.Status = EmailMessageStatus.Dropped; m.Error = Truncate(e.Reason, 500);
                return DroppedForSuppressedAddress(e.Reason) ? EventEffect.HardBounce : EventEffect.SoftStop;
            case EmailEventType.SpamReport: return EventEffect.SpamReport;
            case EmailEventType.Unsubscribe: return EventEffect.Unsubscribe;
        }
        if (e.SmtpId is not null) m.SmtpMessageId ??= e.SmtpId.Trim('<', '>');   // real RFC Message-ID: lets F25 match In-Reply-To headers
        return EventEffect.None;
    }

    private static bool IsHard(EmailEvent e) => e.Status is { } s ? s.StartsWith('5') : true;   // 5.x.x permanent; unknown → treat as hard (safer for reputation)
    private static bool DroppedForSuppressedAddress(string? reason) =>
        reason is not null && new[] { "Bounced Address", "Spam Reporting Address", "Unsubscribed Address", "Invalid" }.Any(k => reason.Contains(k, StringComparison.OrdinalIgnoreCase));
}
public enum EventEffect { None, HardBounce, SoftStop, SpamReport, Unsubscribe }
```
**`EventEffect` → consequences (application layer):**
| Effect | Enrollment | Lead | Suppression list |
|---|---|---|---|
| `HardBounce` | `ApplyStop(Bounced)` | `Bounced` (if `CanTransition`) | add `Bounced` |
| `SoftStop` (blocked, non-suppression drop) | `Failed("Blocked/dropped: <reason>")` | unchanged | — |
| `SpamReport` | `ApplyStop(Unsubscribed)` + all the lead's other enrollments | `Unsubscribed` | add `SpamReport` |
| `Unsubscribe` (only if SendGrid's own tracking were ever enabled) | as F23's unsubscribe use case | `Unsubscribed` | add `Unsubscribed` |

### Application

**`Abstractions/IEventSignatureVerifier.cs`** (new) → `bool Verify(ReadOnlySpan<byte> rawBody, string? timestamp, string? signature)`; implementation `SendGridEventSignatureVerifier` in Infrastructure:
```csharp
public class SendGridEventSignatureVerifier(IOptions<EventWebhookOptions> options, TimeProvider clock) : IEventSignatureVerifier
{
    public bool Verify(ReadOnlySpan<byte> rawBody, string? timestamp, string? signature)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(options.Value.PublicKey)) return false;
        if (!long.TryParse(timestamp, out var seconds)) return false;
        var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age > TimeSpan.FromHours(options.Value.MaxAgeHours) || age < TimeSpan.FromMinutes(-5)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(options.Value.PublicKey), out _);   // key as shown in the SendGrid UI (base64 SPKI)
            var payload = new byte[timestamp.Length + rawBody.Length];
            Encoding.UTF8.GetBytes(timestamp, payload);
            rawBody.CopyTo(payload.AsSpan(timestamp.Length));
            return ecdsa.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }   // malformed input is just "invalid", never a 500
    }
}
```
`EventWebhookOptions` (section `SendGrid:EventWebhook` — keep `PublicKey` in **user-secrets**, `MaxAgeHours = 26` in appsettings); `StartupConfiguration`: public key required when `Email:Provider = SendGrid` outside tests.

**`UseCases/Webhooks/ProcessEmailEventsUseCase.cs`** (new) — body of the endpoint after verification
```csharp
public async Task<EventBatchResult> ExecuteAsync(IReadOnlyList<RawEmailEvent> events, CancellationToken ct)
{
    var result = new EventBatchResult();
    // 1. de-dupe against processed ids (one query) and within the batch
    var fresh = await store.FilterUnprocessedAsync(events.Where(e => e.SgEventId is not null).DistinctBy(e => e.SgEventId).ToList(), ct);
    result.Duplicates = events.Count - fresh.Count;

    // 2. load all referenced messages in one query, scoped by (workspaceId, id) pairs taken from the custom args
    var messages = await store.LoadMessagesAsync(fresh.Select(e => (e.WorkspaceId, e.EmailMessageId)).Where(p => p.WorkspaceId is not null && p.EmailMessageId is not null)!, ct);

    foreach (var raw in fresh.OrderBy(e => e.Timestamp))                      // chronological within the batch; out-of-order across batches is handled by the rules
    {
        var evt = EmailEventMapper.Map(raw);                                 // "delivered"→Delivered, "open"→Open, "bounce" (type bounce|blocked), "dropped", "spamreport", "unsubscribe"|"group_unsubscribe"; everything else → Ignored
        if (evt.Type == EmailEventType.Ignored) { result.Ignored++; continue; }
        if (!messages.TryGetValue(raw.EmailMessageId!.Value, out var message) || message.WorkspaceId != raw.WorkspaceId) { result.UnknownMessage++; logger.LogInformation("Event for unknown/foreign message ignored"); continue; }

        var effect = EmailEventRules.Apply(message, evt);
        await effects.ApplyAsync(message, effect, evt, ct);                  // enrollment/lead/suppression per the table above
        result.Applied++;
    }
    await store.CommitAsync(fresh.Select(e => e.SgEventId!), ct);            // processed ids + all changes in ONE transaction → a failed batch is retried whole by SendGrid, safely
    return result;
}
```
Events without an `sg_event_id` (shouldn't happen) get a synthetic id from `(sg_message_id, event, timestamp)`.

**Analytics**

**`Dtos/Analytics/AnalyticsDtos.cs`** (new)
```csharp
public record AnalyticsQuery(DateOnly? From, DateOnly? To);                       // interpreted in the workspace time zone; default last 30 days; max 366 days

public record AnalyticsTotals(int Sent, int Delivered, int Opened, int Bounced, int ContactedLeads, int RepliedLeads,
                              double? DeliveredRate, double? OpenRate, double? ReplyRate, double? BounceRate)
{ public bool OpensApproximate => true; }
//   DeliveredRate = Delivered / Sent          OpenRate = Opened / Delivered (unique messages; approximate)
//   ReplyRate     = RepliedLeads / ContactedLeads (human replies only)    BounceRate = Bounced / Sent     — each null when its denominator is 0

public record TrendPoint(DateOnly Day, int Sent, int Opened, int Replies);        // days in the workspace zone, zero-filled
public record StepBreakdown(Guid StepId, int Order, string SubjectTemplate, int Sent, int Delivered, int Opened, int Bounced, int Replies);
public record CampaignAnalytics(Guid CampaignId, string Name, CampaignStatus Status, AnalyticsTotals Totals, IReadOnlyList<StepBreakdown> Steps, IReadOnlyList<TrendPoint> Trend);
public record WorkspaceAnalytics(AnalyticsTotals Totals, IReadOnlyList<TrendPoint> Trend, IReadOnlyList<CampaignRow> Campaigns);
public record CampaignRow(Guid CampaignId, string Name, CampaignStatus Status, AnalyticsTotals Totals);
```
**`Analytics/RateMath.cs`** (pure, tested): `Rate(numerator, denominator)` → `null` when `denominator == 0`, otherwise `n / d` clamped to `[0, 1]` (a late event can never push a displayed rate above 100 %).

**`Analytics/DateRange.cs`** (pure): `(fromUtc, toUtcExclusive) Resolve(AnalyticsQuery, TimeZoneInfo zone, DateTime nowUtc)` — local midnight boundaries converted with `SendScheduler.ToUtc` (DST-safe), `To` inclusive in the UI sense (adds one local day), defaults, `From > To` → invalid, > 366 days → invalid.

**`Abstractions/IAnalyticsRepository.cs`** + EF impl `AnalyticsRepository` (all queries `WorkspaceId`-scoped; indexed aggregates, no row materialisation):
```csharp
// totals for a cohort of messages sent in [fromUtc, toUtc) — one grouped query
var cohort = db.EmailMessages.Where(m => m.WorkspaceId == workspaceId && m.SentAt >= fromUtc && m.SentAt < toUtc
                                         && (campaignId == null || m.CampaignId == campaignId));
var totals = await cohort.GroupBy(_ => 1).Select(g => new
{
    Sent = g.Count(),
    Delivered = g.Count(m => m.DeliveredAt != null),
    Opened = g.Count(m => m.OpenedAt != null),
    Bounced = g.Count(m => m.BouncedAt != null || m.Status == EmailMessageStatus.Bounced || m.Status == EmailMessageStatus.Dropped),
    Contacted = g.Select(m => m.EnrollmentId).Distinct().Count()
}).FirstOrDefaultAsync(ct);

// replied leads: enrollments in the cohort whose thread has a human inbound message
var replied = await db.InboxMessages
    .Where(i => i.WorkspaceId == workspaceId && i.Direction == MessageDirection.Inbound && !i.IsAutoReply)
    .Join(db.InboxThreads, i => i.ThreadId, t => t.Id, (i, t) => new { t.EnrollmentId, i.OccurredAt })
    .Where(x => x.EnrollmentId != null && cohort.Any(m => m.EnrollmentId == x.EnrollmentId))
    .Select(x => x.EnrollmentId).Distinct().CountAsync(ct);
```
Trend: group by workspace-local day — computed by projecting `SentAt`/`OpenedAt`/`OccurredAt` to UTC, then bucketing client-side into the zone's days (a few hundred rows max after the date filter and `GROUP BY` hour in SQL; **verify** the plan with the F29.5 perf test rather than writing SQL `AT TIME ZONE`). Per-step: `GroupBy(m => m.StepId)` joined with `CampaignStep` for order/subject; replies per step attribute to the **last step sent before the reply** (documented: `step = the latest EmailMessage for that enrollment with SentAt <= reply.OccurredAt`).

### Api

**`Controllers/V1/Webhooks/SendGridEventsController.cs`** (new)
```csharp
[ApiController]
[AllowAnonymous]
[Route("api/v1/webhooks/sendgrid/events")]
[RequestSizeLimit(8_388_608)]
public class SendGridEventsController(IEventSignatureVerifier verifier, ProcessEmailEventsUseCase process, ILogger<SendGridEventsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);                                             // raw bytes: signature is over these exact bytes
        var body = ms.ToArray();

        if (!verifier.Verify(body, Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"], Request.Headers["X-Twilio-Email-Event-Webhook-Signature"]))
        { logger.LogWarning("Event webhook rejected: bad or missing signature"); return Unauthorized(); }

        List<RawEmailEvent>? events;
        try { events = JsonSerializer.Deserialize<List<RawEmailEvent>>(body, JsonOptions.Web); }
        catch (JsonException) { return BadRequest(); }                                      // signed but not our shape: a real error worth surfacing
        if (events is null) return BadRequest();

        await process.ExecuteAsync(events, ct);
        return Ok();
    }
}
```
`RawEmailEvent` maps `email`, `timestamp`, `event`, `sg_event_id`, `sg_message_id`, `smtp-id`, `type`, `reason`, `status`, and the custom args `emailMessageId`/`workspaceId`/`campaignId` (`[JsonPropertyName]` attributes — SendGrid's names aren't camelCase). Unknown fields ignored. Add the route to the F19 anonymous allow-list. Set in SendGrid → Settings → Mail Settings → **Event Webhook**: URL `https://<tunnel>/api/v1/webhooks/sendgrid/events`, enable *Signed Event Webhook Requests*, copy the **verification key** into `SendGrid:EventWebhook:PublicKey`, events: Delivered, Opened, Bounced, Dropped, Spam Reports, Unsubscribes (Deferred/Processed/Clicked not needed).

**`Controllers/V1/AnalyticsController.cs`** (new, `[Authorize]`)
- `GET api/v1/analytics/workspace/summary?from=&to=` → `WorkspaceAnalytics`
- `GET api/v1/analytics/campaigns/{id}/summary?from=&to=` → `CampaignAnalytics` (`404` for another workspace's campaign)
- Invalid range → `400` (ProblemDetails with a clear message); the workspace time zone comes from `Workspace.TimeZone`.
`Program.cs`: register verifier, use cases, repositories, `Configure<EventWebhookOptions>`.

### Angular (`features/analytics/`, lazy `AnalyticsModule`, replaces the F12 stub)

**`analytics.models.ts`, `analytics.service.ts`** — mirror DTOs; `workspace(range)`, `campaign(id, range)`.

**Shared helpers (pure, unit-tested):** `format-rate.ts` (`formatRate(null) → "—"`, `0.4286 → "42.9%"`, never "NaN%"), `date-range.ts` (presets *Last 7 / 30 / 90 days*, custom; serialises to `from`/`to` `yyyy-MM-dd`; validates ≤ 366 days, from ≤ to), `chart-scales.ts` (nice max/ticks for counts, x positions for N days).

**`analytics-dashboard`** (`/analytics`)
- **Range bar:** preset toggle + `MatDateRangeInput` for custom; selection kept in query params (`?range=30d` / `?from=&to=`).
- **Stat tiles** (`stat-tile` component: label, big value, secondary rate line, optional info icon): **Sent**, **Delivered** (rate of sent), **Opened ≈** (rate of delivered; info tooltip: *"Open counts are approximate. Apple Mail Privacy Protection and image blocking mean some opens are over- or under-counted."*), **Replied** (leads; rate of leads contacted), **Bounced** (rate of sent; warns in text when ≥ 2 %). Values via `formatRate`; `null` rates show "—" with the reason in the tooltip ("no emails delivered in this range").
- **Trend chart** (`trend-chart`): daily Sent / Opened / Replies lines, y-axis counts, x-axis dates (workspace zone), **direct end-labels** instead of a colour legend, distinct marker shapes as a second encoding, hover/focus tooltip with exact numbers, `role="img"` with `<title>` + `<desc>` summarising the trend, and a **View as table** toggle that swaps in an accessible `<table>` of the same data. Colours from CSS variables (`--chart-1…3`) defined for light and dark; empty range → friendly empty state, not an empty axis.
- **Campaigns table:** name, status chip, sent, delivered %, opened ≈ %, replied %, bounced % — row link → campaign view; sortable (`MatSort`, client-side, ≤ a few dozen rows).

**`campaign-analytics`** (`/analytics/campaigns/:id`) — same tiles + trend, then the **per-step breakdown**: a bar list (`bar-list` component: one row per step "Step 2 · *subject…*", horizontal bars for Sent/Delivered/Opened/Replies with numbers printed at the bar ends) plus the same data as a table toggle; link back to the campaign (`/campaigns/:id`). A banner when the campaign is `Draft` ("Nothing sent yet").
- Loading skeletons (`mat-progress-bar`), error + retry, `cdr.markForCheck()`.
- Copy rule everywhere: opens are labelled *≈* / "approx."; replies count only human replies (auto-replies excluded); wording for the ReplyRate denominator: "of leads contacted".

## Tests (priority — PBI 28.4)
**Signature verification** (`SendGridEventSignatureVerifierTests` — generate a P-256 key in the test, sign `timestamp + body` with `SignData(..., DSASignatureFormat.Rfc3279DerSequence)`, export the public key as base64 SPKI):
valid → true; **one flipped byte in the body** → false; wrong timestamp → false; signature from a different key → false; missing/empty headers → false; non-numeric timestamp, garbage base64 signature, garbage public key → false (never throws); timestamp older than `MaxAgeHours` or > 5 min in the future → false, 1 hour old → true (a legitimate SendGrid retry); **re-serialised JSON** (same data, different whitespace) → false — proves verification runs on raw bytes. Controller: bad signature → `401` and `ProcessEmailEventsUseCase` **not** called; valid → `200`.

**Event rules** (`EmailEventRulesTests`, table-driven): delivered → `DeliveredAt` set, status `Delivered`; **open before delivered** → both timestamps set; open twice → `OpenedAt` keeps the first (unique opens); delivered after bounced → stays `Bounced`; bounce after delivered → `Bounced`; `5.1.1` hard vs `4.x` soft vs unknown status (hard); `blocked` → `SoftStop`; dropped with `"Bounced Address"` → `HardBounce`, dropped with another reason → `SoftStop`; `smtp-id` stored once, angle brackets trimmed.

**Use case** (`ProcessEmailEventsUseCaseTests`, SQLite `TestDb`): the **same batch twice** → second run all `Duplicates`, no state change; events out of order across two batches (`open` then `delivered`) end in the same final state as the reverse order; hard bounce → message `Bounced`, enrollment `Bounced`, lead `Bounced`, suppression row, **later sends blocked** (F23 sender test with that enrollment); soft stop → enrollment `Failed`, no suppression; spam report → lead `Unsubscribed`, suppression `SpamReport`, other campaigns' enrollments stopped; unknown message id and **`workspaceId` that doesn't match the message** → ignored, nothing written; ignored event types (`processed`, `deferred`, `click`) don't fail the batch; a batch of 500 events = bounded number of queries (interceptor assertion); a failure mid-batch rolls back **both** effects and processed-id rows, so the retried batch applies cleanly; **cross-workspace:** an event carrying workspace B's id and A's message id changes nothing.

**Analytics math** (`RateMathTests`, `DateRangeTests`, `AnalyticsRepositoryTests`): zero denominators → `null` (not 0, not NaN); numerator > denominator clamped to 100 %; unique opens (two open events, one message → 1); cohort semantics (a message sent before `from` is excluded even if opened inside the range); replied-leads counts distinct enrollments and **excludes auto-replies** and other campaigns; per-step reply attribution to the last-sent step; zero-filled trend across a DST boundary (New York range containing 2026-03-08 has 7 contiguous days); range edges at local midnight; default 30 days; > 366 days and `from > to` → 400. **Cross-workspace (priority):** workspace B's summary excludes A's messages; `campaigns/{A's id}/summary` from B → 404; super admin → 403 (inventory test).

**Angular:** `format-rate.spec.ts` (null → "—", rounding, 0 %), `date-range.spec.ts`, `chart-scales.spec.ts`, `stat-tile.spec.ts` (approx caveat tooltip present on Opened, absent elsewhere), `trend-chart.spec.ts` (renders N points, end labels, table toggle shows identical numbers, `role="img"` with title, empty state), `analytics-dashboard.spec.ts` (range change → request with `from`/`to`; error + retry), `campaign-analytics.spec.ts` (per-step rows ordered by `order`, Draft banner).

## Not in this feature
Click tracking, per-lead engagement timelines, open-time/device breakdowns, export to CSV, scheduled reports, A/B comparisons, alerting on bounce rate (F30.7 covers infrastructure alerts), a chart library, storing the raw event stream.

## Verification
1. `dotnet ef migrations add AddWebhookEventsAndAnalyticsIndexes …`; `dotnet test`; `ng test`.
2. Configure the Event Webhook with the signed option and paste the verification key into user-secrets. Send a real campaign email to your own mailbox; open it. Within a minute the `EmailMessage` shows `DeliveredAt`/`OpenedAt`; the dashboard tiles move (Opened is marked ≈).
3. Use `curl` to replay the captured request body with the original headers → `200`, no changes; change one character of the body → `401`.
4. Send to an invalid address on a domain that rejects (e.g. a non-existent mailbox at a real domain) → bounce event → enrollment `Bounced`, lead `Bounced`, address on the suppression list; importing/enrolling that address is now skipped as suppressed.
5. Open `/analytics`: tiles, trend chart (toggle *View as table*), campaigns table → campaign view with the per-step breakdown; change the range; resize to phone width; check dark mode contrast.
