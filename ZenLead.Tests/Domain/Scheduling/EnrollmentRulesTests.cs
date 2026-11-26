using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Tests.Domain.Scheduling;

public class EnrollmentRulesTests
{
    private static readonly SendWindow Window = new(TimeZoneInfo.Utc, new TimeOnly(9, 0), new TimeOnly(17, 0), SendDays.All);

    private static CampaignStep Step(int order, int delay) => new() { Id = Guid.NewGuid(), Order = order, DelayDays = delay };
    private static DateTime Utc(int d, int h) => new(2026, 3, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Advances_Through_Steps_Then_Completes()
    {
        var steps = new[] { Step(1, 0), Step(2, 3), Step(3, 7) };
        var e = new CampaignEnrollment { NextStepOrder = 1, NextSendAt = Utc(10, 10) };

        EnrollmentRules.AdvanceAfterSend(e, steps, Utc(10, 10), Window);
        Assert.Equal(2, e.NextStepOrder);
        Assert.Equal(Utc(13, 10), e.NextSendAt);
        Assert.Equal(Utc(10, 10), e.LastSentAt);

        EnrollmentRules.AdvanceAfterSend(e, steps, Utc(13, 10), Window);
        Assert.Equal(3, e.NextStepOrder);
        Assert.Equal(Utc(20, 10), e.NextSendAt);

        EnrollmentRules.AdvanceAfterSend(e, steps, Utc(20, 10), Window);
        Assert.Equal(EnrollmentStatus.Completed, e.Status);
        Assert.Equal(4, e.NextStepOrder);
        Assert.Null(e.NextSendAt);
        Assert.Equal(Utc(20, 10), e.LastSentAt);
    }

    [Fact]
    public void Gaps_In_Order_Advance_To_The_Next_Greater_Order()
    {
        var steps = new[] { Step(1, 0), Step(4, 2) };
        var e = new CampaignEnrollment { NextStepOrder = 1 };

        EnrollmentRules.AdvanceAfterSend(e, steps, Utc(10, 10), Window);

        Assert.Equal(4, e.NextStepOrder);
        Assert.Equal(Utc(12, 10), e.NextSendAt);
    }

    [Fact]
    public void Next_Send_Is_Clamped_Into_The_Window()
    {
        var steps = new[] { Step(1, 0), Step(2, 1) };
        var e = new CampaignEnrollment { NextStepOrder = 1 };

        EnrollmentRules.AdvanceAfterSend(e, steps, Utc(10, 16), Window with { End = new TimeOnly(12, 0) });

        Assert.Equal(Utc(12, 9), e.NextSendAt);   // +1 day = 11th 16:00, past the 12:00 end → 12th 09:00
    }

    [Theory]
    [InlineData(EnrollmentStatus.Active, EnrollmentRules.StopEvent.Replied, EnrollmentStatus.Replied)]
    [InlineData(EnrollmentStatus.Active, EnrollmentRules.StopEvent.Unsubscribed, EnrollmentStatus.Unsubscribed)]
    [InlineData(EnrollmentStatus.Active, EnrollmentRules.StopEvent.Bounced, EnrollmentStatus.Bounced)]
    [InlineData(EnrollmentStatus.Paused, EnrollmentRules.StopEvent.Replied, EnrollmentStatus.Replied)]
    [InlineData(EnrollmentStatus.Paused, EnrollmentRules.StopEvent.Unsubscribed, EnrollmentStatus.Unsubscribed)]
    [InlineData(EnrollmentStatus.Paused, EnrollmentRules.StopEvent.Bounced, EnrollmentStatus.Bounced)]
    public void Stop_Applies_To_Live_Enrollments(EnrollmentStatus from, EnrollmentRules.StopEvent stop, EnrollmentStatus expected)
    {
        var e = new CampaignEnrollment { Status = from, NextSendAt = Utc(10, 10) };

        Assert.True(EnrollmentRules.ApplyStop(e, stop));
        Assert.Equal(expected, e.Status);
        Assert.Null(e.NextSendAt);
    }

    [Theory]
    [InlineData(EnrollmentStatus.Completed)]
    [InlineData(EnrollmentStatus.Replied)]
    [InlineData(EnrollmentStatus.Failed)]
    [InlineData(EnrollmentStatus.Unsubscribed)]
    [InlineData(EnrollmentStatus.Bounced)]
    public void Stop_Leaves_Finished_Enrollments_Alone(EnrollmentStatus from)
    {
        var e = new CampaignEnrollment { Status = from, NextSendAt = null };

        Assert.False(EnrollmentRules.ApplyStop(e, EnrollmentRules.StopEvent.Bounced));
        Assert.Equal(from, e.Status);
    }

    [Fact]
    public void IsDue_Only_For_Active_With_Past_NextSendAt()
    {
        var now = Utc(10, 12);
        Assert.True(EnrollmentRules.IsDue(new CampaignEnrollment { Status = EnrollmentStatus.Active, NextSendAt = Utc(10, 11) }, now));
        Assert.True(EnrollmentRules.IsDue(new CampaignEnrollment { Status = EnrollmentStatus.Active, NextSendAt = now }, now));
        Assert.False(EnrollmentRules.IsDue(new CampaignEnrollment { Status = EnrollmentStatus.Active, NextSendAt = Utc(10, 13) }, now));
        Assert.False(EnrollmentRules.IsDue(new CampaignEnrollment { Status = EnrollmentStatus.Active, NextSendAt = null }, now));
        Assert.False(EnrollmentRules.IsDue(new CampaignEnrollment { Status = EnrollmentStatus.Paused, NextSendAt = Utc(10, 11) }, now));
        Assert.False(EnrollmentRules.IsDue(new CampaignEnrollment { Status = EnrollmentStatus.Completed, NextSendAt = Utc(10, 11) }, now));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 1, false)]
    [InlineData(3, 0, true)]
    public void Campaign_Finishes_When_No_Live_Enrollment_Remains(int total, int live, bool finished)
        => Assert.Equal(finished, EnrollmentRules.CampaignIsFinished(total, live));

    [Fact]
    public void ResumeAfterOutOfOffice_reactivates_a_replied_enrollment_after_the_delay()
    {
        var e = new CampaignEnrollment { Status = EnrollmentStatus.Replied, NextStepOrder = 2, NextSendAt = null };

        Assert.True(EnrollmentRules.ResumeAfterOutOfOffice(e, Utc(10, 20), 7, Window));

        Assert.Equal(EnrollmentStatus.Active, e.Status);
        Assert.Equal(Utc(18, 9), e.NextSendAt);                 // +7 days lands at 20:00, after the window → next day's 09:00
        Assert.Equal(2, e.NextStepOrder);                       // the sequence continues where it stopped
    }

    [Theory]
    [InlineData(EnrollmentStatus.Active)]
    [InlineData(EnrollmentStatus.Paused)]
    [InlineData(EnrollmentStatus.Completed)]
    [InlineData(EnrollmentStatus.Unsubscribed)]
    [InlineData(EnrollmentStatus.Bounced)]
    [InlineData(EnrollmentStatus.Failed)]
    public void ResumeAfterOutOfOffice_only_undoes_a_reply_stop(EnrollmentStatus status)
    {
        var e = new CampaignEnrollment { Status = status, NextSendAt = Utc(12, 10) };

        Assert.False(EnrollmentRules.ResumeAfterOutOfOffice(e, Utc(10, 10), 7, Window));

        Assert.Equal(status, e.Status);
        Assert.Equal(Utc(12, 10), e.NextSendAt);
    }
}
