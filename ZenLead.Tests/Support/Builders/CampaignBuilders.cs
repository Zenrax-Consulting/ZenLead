using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support.Builders;

/// <summary>A campaign and its steps. With no <see cref="WithStep"/> call it gets one step (day 0) so it can be activated.</summary>
public sealed class CampaignBuilder(Guid workspaceId)
{
    private readonly Campaign _c = new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "Q1 outreach", Status = CampaignStatus.Draft,
        CreatedAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)
    };

    public CampaignBuilder Named(string name) { _c.Name = name; return this; }
    public CampaignBuilder WithStatus(CampaignStatus status) { _c.Status = status; return this; }
    public CampaignBuilder Active(DateTime activatedAt) { _c.Status = CampaignStatus.Active; _c.ActivatedAt = activatedAt; return this; }
    public CampaignBuilder CreatedBy(Guid userId) { _c.CreatedBy = userId; return this; }
    public CampaignBuilder WithWindow(TimeOnly start, TimeOnly end, SendDays days = SendDays.Weekdays) { _c.WindowStart = start; _c.WindowEnd = end; _c.SendDays = days; return this; }
    public CampaignBuilder InTimeZone(string? ianaId) { _c.TimeZoneId = ianaId; return this; }
    public CampaignBuilder WithDailyCap(int cap) { _c.DailySendCap = cap; return this; }

    /// <param name="delayDays">Days after the previous step; the first step must be 0.</param>
    public CampaignBuilder WithStep(int delayDays, string subject = "Hello {{first_name}}", string body = "Hi {{first_name}},\n\nQuick question.", bool ai = false)
    {
        _c.Steps.Add(new CampaignStep
        {
            Id = Guid.NewGuid(), WorkspaceId = _c.WorkspaceId, CampaignId = _c.Id, Order = _c.Steps.Count + 1, DelayDays = delayDays,
            SubjectTemplate = subject, BodyTemplate = body, UseAiPersonalisation = ai
        });
        return this;
    }

    public Campaign Build()
    {
        if (_c.Steps.Count == 0) WithStep(0);
        return _c;
    }

    public async Task<Campaign> SaveAsync(ZenLeadDbContext db)
    {
        db.Campaigns.Add(Build());
        await db.SaveChangesAsync();
        return _c;
    }
}

public sealed class EnrollmentBuilder(Campaign campaign, Lead lead)
{
    private readonly CampaignEnrollment _e = new()
    {
        Id = Guid.NewGuid(), WorkspaceId = campaign.WorkspaceId, CampaignId = campaign.Id, LeadId = lead.Id, NextStepOrder = 1,
        Status = EnrollmentStatus.Active, EnrolledAt = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)
    };

    public EnrollmentBuilder DueAt(DateTime utc, int stepOrder = 1) { _e.NextSendAt = utc; _e.NextStepOrder = stepOrder; return this; }
    public EnrollmentBuilder WithStatus(EnrollmentStatus status) { _e.Status = status; return this; }
    public EnrollmentBuilder LastSent(DateTime utc) { _e.LastSentAt = utc; return this; }
    public EnrollmentBuilder EnrolledAt(DateTime utc) { _e.EnrolledAt = utc; return this; }

    public CampaignEnrollment Build() => _e;

    public async Task<CampaignEnrollment> SaveAsync(ZenLeadDbContext db)
    {
        db.CampaignEnrollments.Add(_e);
        await db.SaveChangesAsync();
        return _e;
    }
}

/// <summary>A campaign email for an enrollment's step; defaults to Sent.</summary>
public sealed class EmailMessageBuilder(CampaignEnrollment enrollment, CampaignStep step)
{
    private readonly EmailMessage _m = new()
    {
        Id = Guid.NewGuid(), WorkspaceId = enrollment.WorkspaceId, CampaignId = enrollment.CampaignId, EnrollmentId = enrollment.Id,
        StepId = step.Id, LeadId = enrollment.LeadId, Subject = "Hello", Body = "Hi,\n\nQuick question.", Status = EmailMessageStatus.Sent,
        QueuedAt = new DateTime(2026, 1, 6, 10, 0, 0, DateTimeKind.Utc), SentAt = new DateTime(2026, 1, 6, 10, 0, 1, DateTimeKind.Utc),
        ProviderMessageId = $"fake-{Guid.NewGuid():N}"
    };

    public EmailMessageBuilder SentAt(DateTime utc) { _m.QueuedAt = utc.AddSeconds(-1); _m.SentAt = utc; return this; }
    public EmailMessageBuilder WithStatus(EmailMessageStatus status) { _m.Status = status; return this; }
    public EmailMessageBuilder Delivered(DateTime utc) { _m.DeliveredAt = utc; _m.Status = EmailMessageStatus.Delivered; return this; }
    public EmailMessageBuilder Opened(DateTime utc) { _m.OpenedAt = utc; return this; }
    public EmailMessageBuilder Bounced(DateTime utc) { _m.BouncedAt = utc; _m.Status = EmailMessageStatus.Bounced; return this; }
    public EmailMessageBuilder WithSubject(string subject) { _m.Subject = subject; return this; }
    public EmailMessageBuilder WithProviderMessageId(string? id) { _m.ProviderMessageId = id; return this; }

    public EmailMessage Build() => _m;

    public async Task<EmailMessage> SaveAsync(ZenLeadDbContext db)
    {
        db.EmailMessages.Add(_m);
        await db.SaveChangesAsync();
        return _m;
    }
}
