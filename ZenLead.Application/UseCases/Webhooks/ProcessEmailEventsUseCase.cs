using Microsoft.Extensions.Logging;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Webhooks;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;
using ZenLead.Domain.Scheduling;
using ZenLead.Domain.Sending;

namespace ZenLead.Application.UseCases.Webhooks;

public class EventBatchResult
{
    public int Applied { get; set; }
    public int Duplicates { get; set; }
    public int Ignored { get; set; }
    public int UnknownMessage { get; set; }
}

/// <summary>
/// Applies a verified SendGrid Event Webhook batch (F28): updates each <see cref="EmailMessage"/> and the consequences of bounces,
/// blocks, drops, spam reports and unsubscribes. Idempotent on <c>sg_event_id</c>; out-of-order safe (see <see cref="EmailEventRules"/>);
/// all-or-nothing (one commit), so SendGrid's retry of a failed batch is always safe. Correlation is by our own
/// <c>emailMessageId</c> custom arg, and the <c>workspaceId</c> arg must match the message's workspace.
/// </summary>
public class ProcessEmailEventsUseCase(IEmailEventStore store, TimeProvider clock, ILogger<ProcessEmailEventsUseCase> logger)
{
    public const int MaxFailureReasonLength = 500;

    public async Task<EventBatchResult> ExecuteAsync(IReadOnlyList<RawEmailEvent> events, CancellationToken ct)
    {
        var result = new EventBatchResult();
        var now = clock.GetUtcNow().UtcDateTime;

        // 1. de-dupe within the batch and against processed ids (one query)
        var keyed = events.Select(e => (Key: EmailEventMapper.IdempotencyKey(e), Raw: e)).DistinctBy(x => x.Key).ToList();
        var unprocessed = await store.FilterUnprocessedAsync(keyed.Select(x => x.Key).ToList(), ct);
        var fresh = keyed.Where(x => unprocessed.Contains(x.Key)).ToList();
        result.Duplicates = events.Count - fresh.Count;
        if (fresh.Count == 0) return result;

        // 2. every referenced message in one query, scoped by the (workspaceId, emailMessageId) pairs from the custom args
        var pairs = fresh.Where(x => x.Raw.WorkspaceId is not null && x.Raw.EmailMessageId is not null)
            .Select(x => (x.Raw.WorkspaceId!.Value, x.Raw.EmailMessageId!.Value)).Distinct().ToList();
        var messages = pairs.Count == 0 ? new Dictionary<Guid, EmailMessage>() : await store.LoadMessagesAsync(pairs, ct);

        var mapped = fresh.Select(x => (x.Key, x.Raw, Event: EmailEventMapper.Map(x.Raw))).OrderBy(x => x.Raw.Timestamp).ToList();   // chronological within the batch

        // 3. consequences only need enrollments/leads for messages with a consequential event: load them once, not per event
        var consequential = mapped
            .Where(x => x.Event.Type is not (EmailEventType.Delivered or EmailEventType.Open or EmailEventType.Ignored))
            .Select(x => Resolve(messages, x.Raw)).OfType<EmailMessage>().Distinct().ToList();
        var targets = consequential.Count == 0 ? new EventEffectTargets() : await store.LoadEffectTargetsAsync(consequential, ct);

        foreach (var (_, raw, evt) in mapped)
        {
            if (evt.Type == EmailEventType.Ignored)
            {
                result.Ignored++;
                continue;
            }
            if (Resolve(messages, raw) is not { } message)
            {
                result.UnknownMessage++;
                logger.LogInformation("Event webhook: {Event} for an unknown or foreign message ignored", evt.Type);   // ids only, never addresses
                continue;
            }

            var effect = EmailEventRules.Apply(message, evt);
            ApplyEffect(message, effect, evt, raw, targets, now);
            result.Applied++;
        }

        if (!await store.CommitAsync(fresh.Select(x => x.Key).ToList(), ct))
        {
            logger.LogInformation("Event webhook batch was committed concurrently by another delivery; acknowledged as duplicate");
            return new EventBatchResult { Duplicates = events.Count };
        }
        logger.LogInformation("Event webhook batch: {Applied} applied, {Duplicates} duplicate, {Ignored} ignored, {Unknown} unknown",
            result.Applied, result.Duplicates, result.Ignored, result.UnknownMessage);
        return result;
    }

    /// <summary>The message for this event, only if the event's workspace arg matches the message's workspace (defence in depth).</summary>
    private static EmailMessage? Resolve(IReadOnlyDictionary<Guid, EmailMessage> messages, RawEmailEvent raw)
        => raw.EmailMessageId is { } id && raw.WorkspaceId is { } ws && messages.TryGetValue(id, out var m) && m.WorkspaceId == ws ? m : null;

    /// <summary>
    /// <list type="table">
    /// <item><b>HardBounce</b> — enrollment stopped (Bounced), lead Bounced, address suppressed (Bounced).</item>
    /// <item><b>SoftStop</b> (blocked, non-suppression drop, 4.x bounce) — enrollment Failed with the reason; lead and suppression untouched.</item>
    /// <item><b>SpamReport</b> — every live enrollment of the lead stopped (Unsubscribed), lead Unsubscribed, address suppressed (SpamReport).</item>
    /// <item><b>Unsubscribe</b> — as F23's unsubscribe flow: same as a spam report but suppressed as Unsubscribed.</item>
    /// </list>
    /// </summary>
    private void ApplyEffect(EmailMessage message, EventEffect effect, EmailEvent evt, RawEmailEvent raw, EventEffectTargets targets, DateTime now)
    {
        if (effect == EventEffect.None) return;
        targets.Enrollments.TryGetValue(message.EnrollmentId, out var enrollment);
        targets.Leads.TryGetValue(message.LeadId, out var lead);

        switch (effect)
        {
            case EventEffect.HardBounce:
                if (enrollment is not null) EnrollmentRules.ApplyStop(enrollment, EnrollmentRules.StopEvent.Bounced);
                SetLeadStatus(lead, LeadStatus.Bounced, now);
                Suppress(message, lead, raw, SuppressionReason.Bounced, targets, now);
                break;

            case EventEffect.SoftStop:
                if (enrollment is { Status: EnrollmentStatus.Active or EnrollmentStatus.Paused })
                {
                    enrollment.Status = EnrollmentStatus.Failed;
                    enrollment.NextSendAt = null;
                    var reason = $"Blocked/dropped: {evt.Reason ?? "no reason given"}";
                    enrollment.FailureReason = reason.Length <= MaxFailureReasonLength ? reason : reason[..MaxFailureReasonLength];
                }
                break;

            case EventEffect.SpamReport:
            case EventEffect.Unsubscribe:
                if (enrollment is not null) EnrollmentRules.ApplyStop(enrollment, EnrollmentRules.StopEvent.Unsubscribed);
                if (targets.LiveEnrollmentsByLead.TryGetValue(message.LeadId, out var live))
                    foreach (var e in live) EnrollmentRules.ApplyStop(e, EnrollmentRules.StopEvent.Unsubscribed);
                SetLeadStatus(lead, LeadStatus.Unsubscribed, now);
                Suppress(message, lead, raw, effect == EventEffect.SpamReport ? SuppressionReason.SpamReport : SuppressionReason.Unsubscribed, targets, now);
                break;
        }
        logger.LogInformation("Event webhook: {Event} → {Effect} for message {MessageId}", evt.Type, effect, message.Id);
    }

    private static void SetLeadStatus(Lead? lead, LeadStatus to, DateTime now)
    {
        if (lead is null || lead.Status == to || !LeadStatusRules.CanTransition(lead.Status, to)) return;
        lead.Status = to;
        lead.UpdatedAt = now;
    }

    private void Suppress(EmailMessage message, Lead? lead, RawEmailEvent raw, SuppressionReason reason, EventEffectTargets targets, DateTime now)
    {
        var email = lead?.Email ?? raw.Email;                                  // the lead's address (what the sender checks); the event's as a fallback
        if (string.IsNullOrWhiteSpace(email)) return;
        var key = EventEffectTargets.SuppressionKey(message.WorkspaceId, email);
        if (!targets.Suppressed.Add(key)) return;                              // first reason wins, like ISuppressionList.AddAsync
        store.StageSuppression(new SuppressedEmail
        {
            Id = Guid.NewGuid(), WorkspaceId = message.WorkspaceId, Email = email.Trim().ToLowerInvariant(), Reason = reason,
            CreatedAt = now, SourceLeadId = lead?.Id
        });
    }
}
