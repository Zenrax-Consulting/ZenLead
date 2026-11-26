using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Application.Sending;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;
using ZenLead.Domain.Scheduling;
using ZenLead.Domain.Templates;
using static ZenLead.Domain.Scheduling.EnrollmentRules;

namespace ZenLead.Application.UseCases.Sending;

public enum SendOutcome { Sent, Skipped, Stopped, Failed, Blocked, CapReached, Retrying, Repaired }

public class SendRunSummary
{
    public int Sent { get; set; }
    public int Failed { get; set; }
    public int Blocked { get; set; }          // refused by the Restricted-mode recipient allow-list (the enrollment is Failed)
    public int Stopped { get; set; }
    public int Skipped { get; set; }
    public int CapReached { get; set; }
    public int Throttled { get; set; }
    public int Retrying { get; set; }
    public int Repaired { get; set; }
    public int Errors { get; set; }

    public int Total => Sent + Failed + Blocked + Stopped + Skipped + CapReached + Throttled + Retrying + Repaired + Errors;

    public void Count(SendOutcome outcome)
    {
        switch (outcome)
        {
            case SendOutcome.Sent: Sent++; break;
            case SendOutcome.Failed: Failed++; break;
            case SendOutcome.Blocked: Blocked++; break;
            case SendOutcome.Stopped: Stopped++; break;
            case SendOutcome.CapReached: CapReached++; break;
            case SendOutcome.Retrying: Retrying++; break;
            case SendOutcome.Repaired: Repaired++; break;
            default: Skipped++; break;
        }
    }
}

/// <summary>What will actually be sent: footer already appended (after AI, so the model can never drop the unsubscribe link).</summary>
public record RenderedEmail(string Subject, string TextBody, string HtmlBody, bool UsedAi, IReadOnlyList<string> Missing, string UnsubscribeToken);

/// <summary>
/// The campaign sender (F23), run every minute by a Hangfire recurring job.
/// <list type="bullet">
/// <item>At-most-once: the <see cref="EmailMessage"/> row is inserted (Queued) before the provider call and the unique
/// (EnrollmentId, StepId) index makes a second insert impossible. A Queued row found later is never re-sent.</item>
/// <item>Each enrollment is isolated: its own try/catch and its own save; one bad row never stops the batch.</item>
/// <item>AI never blocks a send: personalisation off, budget exhausted or model failure → the plain template is sent.</item>
/// </list>
/// </summary>
public class SendDueEmailsUseCase(
    ISenderStore store,
    ISuppressionList suppression,
    IEmailSender sender,
    IEmailComposer composer,
    IAiBudget budget,
    ITokenUsageTracker usage,
    AiPricing pricing,
    IUnsubscribeTokens unsubscribeTokens,
    IOptions<SendingOptions> sending,
    IOptions<EmailOptions> email,
    IOptions<AppOptions> app,
    TimeProvider clock,
    ILogger<SendDueEmailsUseCase> logger)
{
    public const int MaxSubjectLength = 300;
    public const int MaxErrorLength = 500;
    public const string UncertainError = "Uncertain: the process stopped while this email was being sent, so it may or may not have arrived. Not retried, to avoid a duplicate.";

    private static readonly Regex LineBreaksAndControls = new(@"[\u0000-\u001F\u007F]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@" {2,}", RegexOptions.Compiled);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<SendRunSummary> ExecuteAsync(CancellationToken ct)
    {
        var o = sending.Value;
        var summary = new SendRunSummary();
        var due = await store.GetDueAsync(Now, o.BatchSize, ct);
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                summary.Errors++;                                                       // leave the row for the next run; never stop the batch
                logger.LogError(ex, "Sending failed for enrollment {EnrollmentId}", item.EnrollmentId);
            }
        }

        foreach (var (workspaceId, campaignId) in touchedCampaigns)
        {
            if (ct.IsCancellationRequested) break;
            try { await CompleteCampaignIfDoneAsync(workspaceId, campaignId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not check completion of campaign {CampaignId}", campaignId);
            }
        }
        return summary;
    }

    private async Task<SendOutcome> ProcessAsync(DueEnrollment item, Dictionary<Guid, int> workspaceBudget, CancellationToken ct)
    {
        var ctx = await store.LoadAsync(item.WorkspaceId, item.EnrollmentId, ct);
        var now = Now;
        if (ctx is null || !IsDue(ctx.Enrollment, now) || ctx.Campaign.Status != CampaignStatus.Active) return SendOutcome.Skipped;
        var (e, campaign, lead) = (ctx.Enrollment, ctx.Campaign, ctx.Lead);
        var window = CampaignWindowFactory.For(campaign, ctx.WorkspaceTimeZone, logger);

        var step = campaign.Steps.FirstOrDefault(s => s.Order == e.NextStepOrder);
        if (step is null) return await FailAsync(e, "Step no longer exists.", ct);

        // 0. Crash recovery / idempotency: a message for (enrollment, step) already exists
        var existing = await store.FindMessageAsync(e.Id, step.Id, ct);
        if (existing is not null) return await RepairAsync(ctx, window, existing, now, ct);

        // 1. Stop conditions and suppression: checked at every send, not just at enrollment
        if (lead.DeletedAt is not null) return await FailAsync(e, "The lead was deleted.", ct);
        if (lead.Status == LeadStatus.Replied) return await StopAsync(e, StopEvent.Replied, ct);
        if (LeadStatusRules.IsSuppressed(lead.Status) || await suppression.ContainsAsync(item.WorkspaceId, lead.Email, ct))
            return await StopAsync(e, lead.Status == LeadStatus.Bounced ? StopEvent.Bounced : StopEvent.Unsubscribed, ct);
        if (lead.EmailVerificationStatus == EmailVerificationStatus.Invalid) return await FailAsync(e, "Email address marked invalid.", ct);
        if (!sending.Value.Allows(lead.Email))
        {
            await FailAsync(e, "Blocked by the recipient allow-list (non-production safety).", ct);
            return SendOutcome.Blocked;
        }

        // 2. Caps, by the campaign zone's local day: campaign DailySendCap, then the workspace warm-up cap
        var zone = window.Zone;
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(now, zone).Date;
        var dayStartUtc = LocalMidnightUtc(localDate, zone);
        if (await store.CountMessagesSinceAsync(item.WorkspaceId, campaign.Id, dayStartUtc, ct) >= campaign.DailySendCap)
            return await PostponeToNextDayAsync(e, localDate, window, ct);
        if (!workspaceBudget.TryGetValue(item.WorkspaceId, out var left))
            left = Math.Max(0, sending.Value.WorkspaceDailyCap - await store.CountMessagesSinceAsync(item.WorkspaceId, null, dayStartUtc, ct));
        if (left <= 0)
        {
            workspaceBudget[item.WorkspaceId] = 0;
            return await PostponeToNextDayAsync(e, localDate, window, ct);
        }

        // 3. Content: template, then optional AI personalisation with fallback
        var rendered = await BuildContentAsync(ctx, step, ct);
        if (rendered.Missing.Count > 0)
            return await FailAsync(e, $"Missing value for {string.Join(", ", rendered.Missing.Select(t => "{{" + t + "}}"))}.", ct);

        // 4. Insert BEFORE sending: the unique index is the double-send guard
        var message = new EmailMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = item.WorkspaceId, CampaignId = campaign.Id, EnrollmentId = e.Id, StepId = step.Id, LeadId = lead.Id,
            Subject = rendered.Subject, Body = rendered.TextBody, UsedAi = rendered.UsedAi, Status = EmailMessageStatus.Queued, QueuedAt = now
        };
        if (!await store.TryInsertQueuedAsync(message, ct)) return SendOutcome.Skipped;   // another runner won; the repair path handles it next time

        // 5. Send. An exception here leaves the row Queued: after StaleQueuedMinutes it becomes "Uncertain", never a second send.
        var result = await sender.SendAsync(ToOutbound(rendered, lead, campaign, e, message), ct);

        // 6. Record in ONE save: message → Sent AND enrollment advanced AND lead → Contacted
        if (result.Success)
        {
            message.Status = EmailMessageStatus.Sent;
            message.SentAt = Now;
            message.ProviderMessageId = result.ProviderMessageId;
            MarkSent(ctx, window, message.SentAt.Value);
            await store.AttachOutboundAsync(ctx, message, email.Value.FromAddress, ct);   // inbox thread (F25), same save
            await store.SaveAsync(ct);
            workspaceBudget[item.WorkspaceId] = left - 1;
            return SendOutcome.Sent;
        }
        return await HandleFailureAsync(e, message, result, ct);
    }

    /// <summary>A message for (enrollment, step) already exists. See the table in plans/phase-2-features/23-sending-engine.md.</summary>
    private async Task<SendOutcome> RepairAsync(SendContext ctx, SendWindow window, EmailMessage existing, DateTime now, CancellationToken ct)
    {
        var e = ctx.Enrollment;
        switch (existing.Status)
        {
            case EmailMessageStatus.Sent or EmailMessageStatus.Delivered or EmailMessageStatus.Bounced or EmailMessageStatus.Dropped:
                // the provider accepted it; the process died before the enrollment advanced
                MarkSent(ctx, window, existing.SentAt ?? existing.QueuedAt);
                if (existing.Status != EmailMessageStatus.Dropped)
                    await store.AttachOutboundAsync(ctx, existing, email.Value.FromAddress, ct);   // idempotent: a no-op if it was attached
                await store.SaveAsync(ct);
                logger.LogWarning("Enrollment {EnrollmentId} step {StepId} was already sent; advanced without re-sending.", e.Id, existing.StepId);
                return SendOutcome.Repaired;

            case EmailMessageStatus.Queued when now - existing.QueuedAt < TimeSpan.FromMinutes(sending.Value.StaleQueuedMinutes):
                return SendOutcome.Skipped;                                     // another runner is mid-send

            case EmailMessageStatus.Queued:
                existing.Status = EmailMessageStatus.Failed;
                existing.Error = UncertainError;
                MarkSent(ctx, window, now);                                     // the step is skipped for this lead
                await store.SaveAsync(ct);
                logger.LogWarning("Enrollment {EnrollmentId} step {StepId}: delivery uncertain after an interrupted send; not retried to avoid a duplicate.",
                    e.Id, existing.StepId);
                return SendOutcome.Failed;

            default:                                                            // Failed: a previous terminal failure
                if (e.Status == EnrollmentStatus.Active) return await FailAsync(e, existing.Error ?? "Sending failed.", ct);
                return SendOutcome.Skipped;
        }
    }

    private async Task<SendOutcome> HandleFailureAsync(CampaignEnrollment e, EmailMessage message, SendResult result, CancellationToken ct)
    {
        var o = sending.Value;
        if (result.Retryable)
        {
            // nothing was sent: no trail, try again later
            await store.DeleteMessageAsync(message.Id, ct);
            e.SendAttempts++;
            SendOutcome outcome;
            if (e.SendAttempts < o.MaxAttempts)
            {
                var backoff = o.BackoffMinutes.Length == 0 ? 5 : o.BackoffMinutes[Math.Min(e.SendAttempts - 1, o.BackoffMinutes.Length - 1)];
                e.NextSendAt = Now.AddMinutes(backoff);
                outcome = SendOutcome.Retrying;
            }
            else
            {
                Fail(e, $"Provider unavailable after {o.MaxAttempts} attempts.");
                outcome = SendOutcome.Failed;
            }
            await store.SaveAsync(ct);
            logger.LogWarning("Retryable send failure for enrollment {EnrollmentId} (attempt {Attempt}): {Error}", e.Id, e.SendAttempts, result.Error);
            return outcome;
        }

        message.Status = EmailMessageStatus.Failed;
        message.Error = Truncate(result.Error ?? "Rejected by the email provider.", MaxErrorLength);
        Fail(e, $"Rejected by the email provider: {result.Error ?? "unknown error"}");
        await store.SaveAsync(ct);
        logger.LogWarning("Send rejected for enrollment {EnrollmentId}: {Error}", e.Id, result.Error);
        return SendOutcome.Failed;
    }

    private async Task<RenderedEmail> BuildContentAsync(SendContext ctx, CampaignStep step, CancellationToken ct)
    {
        var (e, campaign, lead) = (ctx.Enrollment, ctx.Campaign, ctx.Lead);
        var workspaceId = e.WorkspaceId;
        var values = TemplateRenderer.ValuesFor(lead);
        var subject = TemplateRenderer.Render(step.SubjectTemplate, values);
        var body = TemplateRenderer.Render(step.BodyTemplate, values);
        var missing = subject.MissingTokens.Concat(body.MissingTokens).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var usedAi = false;
        var finalSubject = subject.Text;
        var finalBody = body.Text;

        if (missing.Count == 0 && step.UseAiPersonalisation)
        {
            // Warning (≥ 80%) still personalises; only an exhausted budget falls back to the template
            if (await budget.CheckAsync(workspaceId, ct) == BudgetState.Exceeded)
                logger.LogInformation("AI budget exhausted for workspace {WorkspaceId}; sending the template as-is.", workspaceId);
            else
            {
                try
                {
                    var composed = await composer.ComposeAsync(new EmailComposeContext(lead.Name, lead.Email, lead.Title,
                        AdditionalContext: $"Personalise this email for the recipient. Keep its intent, call to action and under 120 words.\n---\nSubject: {subject.Text}\n\n{body.Text}\n---",
                        lead.Company?.Name, lead.Company?.Industry, lead.Company?.Country), ct);
                    await RecordUsageAsync(workspaceId, campaign.Id, e.Id, composed.PromptTokens, composed.CompletionTokens, true, ct);
                    if (!string.IsNullOrWhiteSpace(composed.Subject) && !string.IsNullOrWhiteSpace(composed.Body))
                    {
                        finalSubject = composed.Subject;
                        finalBody = composed.Body;
                        usedAi = true;
                    }
                    else logger.LogWarning("AI personalisation returned an empty draft; sending the template as-is.");
                }
                catch (AiProviderException ex)
                {
                    if (ex.PromptTokens + ex.CompletionTokens > 0)                   // billed even though unusable: the spend log must show it
                        await RecordUsageAsync(workspaceId, campaign.Id, e.Id, ex.PromptTokens, ex.CompletionTokens, false, ct);
                    logger.LogWarning("AI personalisation failed ({Kind}); sending the template as-is.", ex.Kind);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("AI personalisation timed out; sending the template as-is.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "AI personalisation failed unexpectedly; sending the template as-is.");
                }
            }
        }

        // the footer is appended AFTER AI so the model can never drop or alter the unsubscribe link
        var token = unsubscribeTokens.Create(workspaceId, lead.Id);
        var url = UnsubscribePageUrl(token);
        return new RenderedEmail(FlattenSubject(finalSubject), UnsubscribeFooter.Append(finalBody, url),
            UnsubscribeFooter.AppendHtml(HtmlFromText(finalBody), url), usedAi, missing, token);
    }

    private OutboundEmail ToOutbound(RenderedEmail rendered, Lead lead, Campaign campaign, CampaignEnrollment e, EmailMessage message)
    {
        var o = email.Value;
        return new OutboundEmail(
            To: new EmailAddress(lead.Email, string.IsNullOrWhiteSpace(lead.Name) ? null : lead.Name),
            Subject: rendered.Subject,
            TextBody: rendered.TextBody,
            HtmlBody: rendered.HtmlBody,
            From: new EmailAddress(o.FromAddress, campaign.FromName ?? o.FromName),
            ReplyTo: new EmailAddress(ReplyAddress.Encode(e.Id, o.InboundDomain)),
            Headers: new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = $"<{OneClickUnsubscribeUrl(rendered.UnsubscribeToken)}>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
            },
            CustomArgs: new Dictionary<string, string>
            {
                ["emailMessageId"] = message.Id.ToString(),
                ["workspaceId"] = message.WorkspaceId.ToString(),
                ["campaignId"] = message.CampaignId.ToString()
            },
            Categories: ["campaign"],
            TrackOpens: true);
    }

    private async Task CompleteCampaignIfDoneAsync(Guid workspaceId, Guid campaignId, CancellationToken ct)
    {
        var campaign = await store.GetCampaignAsync(workspaceId, campaignId, ct);
        if (campaign is not { Status: CampaignStatus.Active }) return;
        // touched campaigns had a due enrollment this run, so they have ≥ 1 enrollment (EnrollmentRules.CampaignIsFinished)
        if (await store.CampaignHasLiveEnrollmentsAsync(workspaceId, campaignId, ct)) return;
        campaign.Status = CampaignStatus.Completed;
        campaign.UpdatedAt = Now;
        await store.SaveAsync(ct);
        logger.LogInformation("Campaign {CampaignId} completed.", campaignId);
    }

    private void MarkSent(SendContext ctx, SendWindow window, DateTime sentAtUtc)
    {
        AdvanceAfterSend(ctx.Enrollment, ctx.Campaign.Steps.OrderBy(s => s.Order).ToList(), sentAtUtc, window);
        ctx.Enrollment.SendAttempts = 0;
        if (ctx.Lead.DeletedAt is null && LeadStatusRules.CanTransition(ctx.Lead.Status, LeadStatus.Contacted)) ctx.Lead.Status = LeadStatus.Contacted;
    }

    private async Task<SendOutcome> FailAsync(CampaignEnrollment e, string reason, CancellationToken ct)
    {
        Fail(e, reason);
        await store.SaveAsync(ct);
        return SendOutcome.Failed;
    }

    private async Task<SendOutcome> StopAsync(CampaignEnrollment e, StopEvent stop, CancellationToken ct)
    {
        ApplyStop(e, stop);
        await store.SaveAsync(ct);
        return SendOutcome.Stopped;
    }

    /// <summary>A capped enrollment waits for the next local day's window instead of being re-read (and crowding the batch) every minute.</summary>
    private async Task<SendOutcome> PostponeToNextDayAsync(CampaignEnrollment e, DateTime localDate, SendWindow window, CancellationToken ct)
    {
        e.NextSendAt = SendScheduler.NextSendAt(LocalMidnightUtc(localDate.AddDays(1), window.Zone), 0, window);
        await store.SaveAsync(ct);
        return SendOutcome.CapReached;
    }

    private static void Fail(CampaignEnrollment e, string reason)
    {
        e.Status = EnrollmentStatus.Failed;
        e.FailureReason = Truncate(reason, MaxErrorLength);
        e.NextSendAt = null;
    }

    /// <summary>UTC instant of local midnight on <paramref name="localDate"/> (or the first valid instant after it, if midnight falls in a DST gap).</summary>
    public static DateTime LocalMidnightUtc(DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    private string UnsubscribePageUrl(string token) => $"{app.Value.PublicBaseUrl.TrimEnd('/')}/unsubscribe/{token}";
    private string OneClickUnsubscribeUrl(string token) => $"{app.Value.PublicBaseUrl.TrimEnd('/')}/api/v1/unsubscribe/{token}";

    private Task RecordUsageAsync(Guid workspaceId, Guid campaignId, Guid enrollmentId, int prompt, int completion, bool succeeded, CancellationToken ct)
        => usage.RecordAsync(new AiUsageEntry(workspaceId, pricing.Model, prompt, completion, pricing.EstimateCostUsd(prompt, completion),
            AiPurpose.Personalise, campaignId, enrollmentId, succeeded), ct);

    /// <summary>Single line (no header injection), no runs of spaces, at most 300 chars. Applied to template and AI subjects alike.</summary>
    public static string FlattenSubject(string subject)
    {
        var flat = Spaces.Replace(LineBreaksAndControls.Replace(subject, " "), " ").Trim();
        return flat.Length <= MaxSubjectLength ? flat : flat[..MaxSubjectLength].TrimEnd();
    }

    public static string HtmlFromText(string text)
        => "<p>" + WebUtility.HtmlEncode(text.Replace("\r\n", "\n")).Replace("\n\n", "</p><p>").Replace("\n", "<br>") + "</p>";

    private static string Truncate(string v, int max) => v.Length <= max ? v : v[..max];
}
