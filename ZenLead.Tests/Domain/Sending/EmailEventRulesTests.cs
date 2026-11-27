using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Sending;

namespace ZenLead.Tests.Domain.Sending;

public class EmailEventRulesTests
{
    private static readonly DateTime T0 = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    private static EmailMessage Sent() => new() { Id = Guid.NewGuid(), Status = EmailMessageStatus.Sent, SentAt = T0 };

    private static EmailEvent E(EmailEventType type, int minutes, string? reason = null, string? status = null, string? smtpId = null)
        => new(type, T0.AddMinutes(minutes), reason, status, smtpId);

    [Fact]
    public void Delivered_sets_delivered_at_and_status()
    {
        var m = Sent();
        Assert.Equal(EventEffect.None, EmailEventRules.Apply(m, E(EmailEventType.Delivered, 1)));
        Assert.Equal(T0.AddMinutes(1), m.DeliveredAt);
        Assert.Equal(EmailMessageStatus.Delivered, m.Status);
    }

    [Fact]
    public void Open_before_delivered_sets_both_timestamps()
    {
        var m = Sent();
        EmailEventRules.Apply(m, E(EmailEventType.Open, 5));
        Assert.Equal(T0.AddMinutes(5), m.OpenedAt);
        Assert.Equal(T0.AddMinutes(5), m.DeliveredAt);
        Assert.Equal(EmailMessageStatus.Delivered, m.Status);

        EmailEventRules.Apply(m, E(EmailEventType.Delivered, 1));             // the late delivered event: earliest time wins
        Assert.Equal(T0.AddMinutes(1), m.DeliveredAt);
    }

    [Fact]
    public void Either_order_of_open_and_delivered_ends_in_the_same_state()
    {
        var a = Sent();
        EmailEventRules.Apply(a, E(EmailEventType.Delivered, 1));
        EmailEventRules.Apply(a, E(EmailEventType.Open, 5));
        var b = Sent();
        EmailEventRules.Apply(b, E(EmailEventType.Open, 5));
        EmailEventRules.Apply(b, E(EmailEventType.Delivered, 1));
        Assert.Equal((a.Status, a.DeliveredAt, a.OpenedAt), (b.Status, b.DeliveredAt, b.OpenedAt));
    }

    [Fact]
    public void Open_twice_keeps_the_first_open()
    {
        var m = Sent();
        EmailEventRules.Apply(m, E(EmailEventType.Open, 10));
        EmailEventRules.Apply(m, E(EmailEventType.Open, 30));
        Assert.Equal(T0.AddMinutes(10), m.OpenedAt);
    }

    [Fact]
    public void Delivered_after_bounced_stays_bounced()
    {
        var m = Sent();
        EmailEventRules.Apply(m, E(EmailEventType.Bounce, 1, "550 no such user", "5.1.1"));
        EmailEventRules.Apply(m, E(EmailEventType.Delivered, 2));
        EmailEventRules.Apply(m, E(EmailEventType.Open, 3));
        Assert.Equal(EmailMessageStatus.Bounced, m.Status);
    }

    [Fact]
    public void Bounce_after_delivered_becomes_bounced()
    {
        var m = Sent();
        EmailEventRules.Apply(m, E(EmailEventType.Delivered, 1));
        var effect = EmailEventRules.Apply(m, E(EmailEventType.Bounce, 60, "550 mailbox gone", "5.1.1"));
        Assert.Equal(EventEffect.HardBounce, effect);
        Assert.Equal(EmailMessageStatus.Bounced, m.Status);
        Assert.Equal(T0.AddMinutes(60), m.BouncedAt);
        Assert.Equal("550 mailbox gone", m.Error);
    }

    [Theory]
    [InlineData("5.1.1", EventEffect.HardBounce)]
    [InlineData("5.0.0", EventEffect.HardBounce)]
    [InlineData("4.2.2", EventEffect.SoftStop)]
    [InlineData("4.0.0", EventEffect.SoftStop)]
    [InlineData(null, EventEffect.HardBounce)]                                 // unknown → hard (safer for reputation)
    [InlineData("", EventEffect.HardBounce)]
    public void Bounce_status_decides_hard_or_soft(string? status, EventEffect expected)
        => Assert.Equal(expected, EmailEventRules.Apply(Sent(), E(EmailEventType.Bounce, 1, "reason", status)));

    [Fact]
    public void Blocked_is_a_soft_stop()
    {
        var m = Sent();
        Assert.Equal(EventEffect.SoftStop, EmailEventRules.Apply(m, E(EmailEventType.Blocked, 1, "IP blocklisted", "5.7.1")));
        Assert.Equal(EmailMessageStatus.Bounced, m.Status);
    }

    [Theory]
    [InlineData("Bounced Address", EventEffect.HardBounce)]
    [InlineData("Spam Reporting Address", EventEffect.HardBounce)]
    [InlineData("Unsubscribed Address", EventEffect.HardBounce)]
    [InlineData("Invalid", EventEffect.HardBounce)]
    [InlineData("Exceeded max message size", EventEffect.SoftStop)]
    [InlineData(null, EventEffect.SoftStop)]
    public void Dropped_reason_decides_hard_or_soft(string? reason, EventEffect expected)
    {
        var m = Sent();
        Assert.Equal(expected, EmailEventRules.Apply(m, E(EmailEventType.Dropped, 1, reason)));
        Assert.Equal(EmailMessageStatus.Dropped, m.Status);
    }

    [Fact]
    public void Spam_report_and_unsubscribe_return_their_effects_without_changing_delivery_state()
    {
        var m = Sent();
        Assert.Equal(EventEffect.SpamReport, EmailEventRules.Apply(m, E(EmailEventType.SpamReport, 1)));
        Assert.Equal(EventEffect.Unsubscribe, EmailEventRules.Apply(m, E(EmailEventType.Unsubscribe, 1)));
        Assert.Equal(EmailMessageStatus.Sent, m.Status);
    }

    [Fact]
    public void Smtp_id_is_stored_once_with_angle_brackets_trimmed()
    {
        var m = Sent();
        EmailEventRules.Apply(m, E(EmailEventType.Delivered, 1, smtpId: "<abc.123@ismtpd-1>"));
        EmailEventRules.Apply(m, E(EmailEventType.Open, 2, smtpId: "<other@ismtpd-2>"));
        Assert.Equal("abc.123@ismtpd-1", m.SmtpMessageId);

        var bounced = Sent();
        EmailEventRules.Apply(bounced, E(EmailEventType.Bounce, 1, "x", "5.1.1", "<b@x>"));
        Assert.Equal("b@x", bounced.SmtpMessageId);                            // stored on consequential events too
    }

    [Fact]
    public void Long_reasons_are_truncated()
    {
        var m = Sent();
        EmailEventRules.Apply(m, E(EmailEventType.Bounce, 1, new string('r', 900), "5.1.1"));
        Assert.Equal(500, m.Error!.Length);
    }
}
