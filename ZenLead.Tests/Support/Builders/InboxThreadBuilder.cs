using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support.Builders;

/// <summary>
/// An inbox thread for a lead, optionally tied to a campaign enrollment, with an outbound first message and any replies added with
/// <see cref="WithReply"/>. The thread is unread while it has a reply. For full conversations in one call, <c>InboxSeed</c> still works.
/// </summary>
public sealed class InboxThreadBuilder
{
    private readonly InboxThread _t;
    private readonly string _leadEmail;
    private readonly List<InboxMessage> _messages = [];

    public InboxThreadBuilder(Lead lead, CampaignEnrollment? enrollment = null)
    {
        _leadEmail = lead.Email;
        var at = new DateTime(2026, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        _t = new InboxThread
        {
            Id = Guid.NewGuid(), WorkspaceId = lead.WorkspaceId, LeadId = lead.Id, CampaignId = enrollment?.CampaignId, EnrollmentId = enrollment?.Id,
            Subject = "Quick question", LastMessageAt = at, IsRead = true, CreatedAt = at
        };
        _messages.Add(Message(MessageDirection.Outbound, at, _t.Subject, "Hi, would a short call be useful?"));
    }

    public InboxThreadBuilder WithSubject(string subject) { _t.Subject = subject; _messages[0].Subject = subject; return this; }

    public InboxThreadBuilder StartedAt(DateTime utc)
    {
        _t.CreatedAt = _t.LastMessageAt = _messages[0].OccurredAt = utc;
        return this;
    }

    public InboxThreadBuilder WithReply(string text, DateTime? at = null, ReplyClassification label = ReplyClassification.Unclassified,
        decimal? confidence = null, bool needsReview = false, string? messageId = null)
    {
        var when = at ?? _t.LastMessageAt.AddHours(1);
        var reply = Message(MessageDirection.Inbound, when, $"Re: {_t.Subject}", text);
        reply.Classification = label;
        reply.ClassificationConfidence = confidence;
        reply.ClassifiedBy = label == ReplyClassification.Unclassified ? ClassificationSource.None : ClassificationSource.Ai;
        reply.NeedsReview = needsReview;
        reply.RawMessageId = messageId ?? $"reply-{Guid.NewGuid():N}@example.test";
        _messages.Add(reply);
        _t.LastMessageAt = when;
        _t.IsRead = false;
        _t.Classification = label;
        return this;
    }

    public InboxThreadBuilder Read() { _t.IsRead = true; return this; }

    public InboxThread Build() => _t;
    public IReadOnlyList<InboxMessage> Messages => _messages;

    public async Task<InboxThread> SaveAsync(ZenLeadDbContext db)
    {
        db.InboxThreads.Add(_t);
        db.InboxMessages.AddRange(_messages);
        await db.SaveChangesAsync();
        return _t;
    }

    private InboxMessage Message(MessageDirection direction, DateTime at, string subject, string text) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = _t.WorkspaceId, ThreadId = _t.Id, Direction = direction, Subject = subject, TextBody = text, OccurredAt = at,
        FromAddress = direction == MessageDirection.Inbound ? _leadEmail : "business@zenraxconsulting.com",
        ToAddress = direction == MessageDirection.Inbound ? $"r-{_t.Id:N}@reply.example.test" : _leadEmail,
        RawMessageId = direction == MessageDirection.Outbound ? $"sent-{Guid.NewGuid():N}@example.test" : null
    };
}
