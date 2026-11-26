using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Leads;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Application.Classification;

/// <summary>
/// Records a reply's label and applies its consequences. Shared by the classification job (F26) and the manual override (F27) so
/// both follow identical rules. Only two labels act, and only when confident (or chosen by a human):
/// <list type="bullet">
/// <item><b>Unsubscribe</b> — lead → Unsubscribed, email suppressed, every live enrollment of the lead stopped. The reply's own
/// enrollment is already <c>Replied</c> (F25) and stays so — it is stopped either way; don't "fix" that.</item>
/// <item><b>OutOfOffice</b> — undo F25's reply-stop: the sequence resumes after <c>OooResumeDelayDays</c> and the lead goes back to
/// Contacted, unless the thread has another human reply.</item>
/// </list>
/// Suppression is sticky: relabelling an unsubscribe as something else never un-suppresses (that is a separate, deliberate flow).
/// </summary>
public class ClassificationEffects(IInboxClassificationStore store, ISuppressionList suppression, IOptions<ClassificationOptions> options, TimeProvider clock)
{
    /// <param name="manual">A human chose this label: confidence is 1 and the threshold doesn't apply.</param>
    /// <param name="note">Overrides the stored <c>ClassificationNote</c>; defaults to <c>low_confidence</c> when no action was taken.</param>
    public async Task ApplyAsync(Guid workspaceId, Guid messageId, ReplyClassification label, decimal confidence, bool manual, string? note, CancellationToken ct)
    {
        var ctx = await store.LoadAsync(workspaceId, messageId, ct);
        if (ctx is null) return;
        var min = options.Value.AutoActionMinConfidence;
        var act = manual || confidence >= min;
        var now = clock.GetUtcNow().UtcDateTime;

        ctx.Message.Classification = label;
        ctx.Message.ClassificationConfidence = manual ? 1m : confidence;
        ctx.Message.ClassifiedBy = manual ? ClassificationSource.Manual : ClassificationSource.Ai;
        ctx.Message.NeedsReview = !manual && !act && label is ReplyClassification.Unsubscribe or ReplyClassification.OutOfOffice;   // uncertain AND consequential
        ctx.Message.ClassificationNote = note ?? (act ? null : ClassificationNotes.LowConfidence);
        if (ctx.IsLatestInbound) ctx.Thread.Classification = label;                          // the latest human reply's label drives the list chip

        if (act && label == ReplyClassification.Unsubscribe)
        {
            if (ctx.Enrollment is not null) EnrollmentRules.ApplyStop(ctx.Enrollment, EnrollmentRules.StopEvent.Unsubscribed);   // no-op: F25 already made it Replied
            await store.StopAllEnrollmentsForLeadAsync(workspaceId, ctx.Lead.Id, EnrollmentRules.StopEvent.Unsubscribed, ct);
            if (ctx.Lead.Status != LeadStatus.Unsubscribed && LeadStatusRules.CanTransition(ctx.Lead.Status, LeadStatus.Unsubscribed))
            {
                ctx.Lead.Status = LeadStatus.Unsubscribed;
                ctx.Lead.UpdatedAt = now;
            }
            await store.SaveAsync(ct);
            await suppression.AddAsync(workspaceId, ctx.Lead.Email, SuppressionReason.Unsubscribed, ctx.Lead.Id, ct);   // idempotent upsert; saves
            return;
        }

        if (act && label == ReplyClassification.OutOfOffice && ctx.Enrollment is not null && !LeadStatusRules.IsSuppressed(ctx.Lead.Status)
            && !await store.ThreadHasOtherHumanReplyAsync(ctx.Thread.Id, ctx.Message.Id, ct))
        {
            if (EnrollmentRules.ResumeAfterOutOfOffice(ctx.Enrollment, now, options.Value.OooResumeDelayDays, ctx.Window)
                && LeadStatusRules.CanRevertReply(ctx.Lead.Status, LeadStatus.Contacted))
            {
                ctx.Lead.Status = LeadStatus.Contacted;
                ctx.Lead.UpdatedAt = now;
            }
        }
        await store.SaveAsync(ct);
    }
}
