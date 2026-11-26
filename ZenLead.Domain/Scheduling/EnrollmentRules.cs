using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Domain.Scheduling;

public static class EnrollmentRules
{
    /// <summary>
    /// After step <see cref="CampaignEnrollment.NextStepOrder"/> was sent at <paramref name="sentAtUtc"/>: schedule the next
    /// step (the next greater <c>Order</c>, so gaps left by edits are tolerated) or complete the enrollment.
    /// F23 calls this inside the same transaction that marks the message sent.
    /// </summary>
    public static void AdvanceAfterSend(CampaignEnrollment e, IReadOnlyList<CampaignStep> steps, DateTime sentAtUtc, SendWindow window)
    {
        e.LastSentAt = sentAtUtc;
        var next = steps.Where(s => s.Order > e.NextStepOrder).OrderBy(s => s.Order).FirstOrDefault();
        if (next is null)
        {
            e.NextStepOrder = (steps.Count == 0 ? e.NextStepOrder : Math.Max(steps.Max(s => s.Order), e.NextStepOrder)) + 1;
            e.NextSendAt = null;
            e.Status = EnrollmentStatus.Completed;
            return;
        }
        e.NextStepOrder = next.Order;
        e.NextSendAt = SendScheduler.NextSendAt(sentAtUtc, next.DelayDays, window);
    }

    public enum StopEvent { Replied, Unsubscribed, Bounced }

    /// <summary>Stop conditions only act on live enrollments; finished ones keep their final status. Returns whether anything changed.</summary>
    public static bool ApplyStop(CampaignEnrollment e, StopEvent stop)
    {
        if (e.Status is not (EnrollmentStatus.Active or EnrollmentStatus.Paused)) return false;
        e.Status = stop switch
        {
            StopEvent.Replied => EnrollmentStatus.Replied,
            StopEvent.Unsubscribed => EnrollmentStatus.Unsubscribed,
            _ => EnrollmentStatus.Bounced
        };
        e.NextSendAt = null;
        return true;
    }

    /// <summary>Undo a reply-stop caused by an out-of-office: the sequence continues after <paramref name="resumeAfterDays"/> days.</summary>
    public static bool ResumeAfterOutOfOffice(CampaignEnrollment e, DateTime nowUtc, int resumeAfterDays, SendWindow window)
    {
        if (e.Status != EnrollmentStatus.Replied) return false;
        e.Status = EnrollmentStatus.Active;
        e.NextSendAt = SendScheduler.NextSendAt(nowUtc, resumeAfterDays, window);
        return true;
    }

    public static bool IsDue(CampaignEnrollment e, DateTime nowUtc)
        => e.Status == EnrollmentStatus.Active && e.NextSendAt is { } at && at <= nowUtc;

    /// <summary>
    /// The completion rule F23's sender applies: an Active campaign becomes Completed once it has at least one enrollment
    /// and none of them is still live (Active or Paused).
    /// </summary>
    public static bool CampaignIsFinished(int totalEnrollments, int liveEnrollments) => totalEnrollments > 0 && liveEnrollments == 0;
}
