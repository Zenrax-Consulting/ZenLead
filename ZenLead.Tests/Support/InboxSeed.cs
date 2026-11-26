using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support;

/// <summary>Builds inbox conversations (lead + campaign + enrollment + thread + messages) for F27 tests. Rows carry their WorkspaceId explicitly.</summary>
public static class InboxSeed
{
    public sealed record Conversation(Lead Lead, Campaign Campaign, CampaignEnrollment Enrollment, InboxThread Thread);

    /// <summary>
    /// A campaign email at <paramref name="at"/> and, unless <paramref name="replyText"/> is null, the lead's reply one hour later
    /// (Message-ID <c>reply-{n}@mail.test</c>). The thread is unread when there is a reply.
    /// </summary>
    public static async Task<Conversation> ConversationAsync(ZenLeadDbContext db, Guid workspaceId, DateTime at,
        string leadName = "Jane Doe", string subject = "Quick question", string? replyText = "Sounds interesting, tell me more.",
        LeadStatus status = LeadStatus.Replied, string campaignName = "Q4 outreach", string? company = "Acme",
        ReplyClassification classification = ReplyClassification.Unclassified, bool needsReview = false, bool autoReply = false,
        string? leadEmail = null, DateTime? deletedAt = null, Campaign? campaign = null)
    {
        Company? co = null;
        if (company is not null)
        {
            co = new Company { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = company, CreatedAt = at };
            db.Companies.Add(co);
        }
        var lead = new Lead
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = leadName, Email = leadEmail ?? $"lead.{Guid.NewGuid():N}@acme.test",
            Status = status, CompanyId = co?.Id, CreatedAt = at, DeletedAt = deletedAt
        };
        db.Leads.Add(lead);
        if (campaign is null)
        {
            campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = campaignName, Status = CampaignStatus.Active, CreatedAt = at };
            db.Campaigns.Add(campaign);
        }
        var enrollment = new CampaignEnrollment
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, CampaignId = campaign.Id, LeadId = lead.Id,
            Status = replyText is null ? EnrollmentStatus.Active : EnrollmentStatus.Replied, NextSendAt = at.AddDays(3), EnrolledAt = at
        };
        db.CampaignEnrollments.Add(enrollment);
        var thread = new InboxThread
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, LeadId = lead.Id, CampaignId = campaign.Id, EnrollmentId = enrollment.Id,
            Subject = subject, LastMessageAt = replyText is null ? at : at.AddHours(1), IsRead = replyText is null,
            Classification = classification, CreatedAt = at
        };
        db.InboxThreads.Add(thread);
        db.InboxMessages.Add(Outbound(workspaceId, thread.Id, at, subject, "Hi, would you like a demo?", lead.Email, $"sent-{Guid.NewGuid():N}@mail.test"));
        if (replyText is not null)
        {
            var reply = Inbound(workspaceId, thread.Id, at.AddHours(1), $"Re: {subject}", replyText, lead.Email, $"reply-{Guid.NewGuid():N}@mail.test");
            reply.Classification = classification;
            reply.NeedsReview = needsReview;
            reply.IsAutoReply = autoReply;
            db.InboxMessages.Add(reply);
        }
        await db.SaveChangesAsync();
        return new Conversation(lead, campaign, enrollment, thread);
    }

    public static InboxMessage Inbound(Guid workspaceId, Guid threadId, DateTime at, string subject, string text, string from, string? messageId)
        => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, ThreadId = threadId, Direction = MessageDirection.Inbound,
            FromAddress = from, FromName = "Lead", ToAddress = "r-x@reply.zenlead.test", Subject = subject, TextBody = text,
            OccurredAt = at, RawMessageId = messageId
        };

    public static InboxMessage Outbound(Guid workspaceId, Guid threadId, DateTime at, string subject, string text, string to, string? messageId)
        => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, ThreadId = threadId, Direction = MessageDirection.Outbound,
            FromAddress = "business@zenraxconsulting.com", ToAddress = to, Subject = subject, TextBody = text, OccurredAt = at,
            RawMessageId = messageId
        };
}
