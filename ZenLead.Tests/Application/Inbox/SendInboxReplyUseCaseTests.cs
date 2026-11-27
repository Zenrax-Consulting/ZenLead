using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Dtos.Inbox;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.Email;
using ZenLead.Application.UseCases.Inbox;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using ZenLead.Domain.Templates;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Inbox;

/// <summary>F27: manual replies over the real inbox repository on SQLite, with the fake sender (nothing leaves the process).</summary>
public sealed class SendInboxReplyUseCaseTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(T0.AddDays(1)));
    private readonly FakeEmailSender _sender = new();
    private readonly FakeSuppressionList _suppression = new();
    private readonly EmailOptions _email = new() { FromAddress = "business@zenraxconsulting.com", FromName = "Zenrax", InboundDomain = "reply.zenlead.test" };

    public SendInboxReplyUseCaseTests()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = _a, Name = "A", CreatedAt = T0 });
        ctx.Workspaces.Add(new Workspace { Id = _b, Name = "B", CreatedAt = T0 });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private SendInboxReplyUseCase UseCase(Guid workspace, Func<IInboxRepository, IInboxRepository>? wrap = null)
    {
        IInboxRepository repo = new InboxRepository(_db.CreateContext(workspace));
        return new SendInboxReplyUseCase(wrap?.Invoke(repo) ?? repo, _suppression, _sender, Options.Create(_email), _clock);
    }

    private Task<ReplyOutcome> Reply(Guid threadId, string body = "Happy to set up a call. Does Tuesday work?", Guid? requestId = null, Guid? workspace = null)
        => UseCase(workspace ?? _a).ExecuteAsync(workspace ?? _a, _user, threadId, new ReplyRequest(requestId ?? Guid.NewGuid(), body), default);

    private async Task<InboxSeed.Conversation> Seed(string subject = "Quick question", LeadStatus status = LeadStatus.Replied, DateTime? deletedAt = null)
    {
        await using var ctx = _db.CreateContext(null);
        return await InboxSeed.ConversationAsync(ctx, _a, T0, subject: subject, status: status, deletedAt: deletedAt);
    }

    private async Task<List<InboxMessage>> Messages(Guid threadId)
    {
        await using var ctx = _db.CreateContext(null);
        return await ctx.InboxMessages.IgnoreQueryFilters().Where(m => m.ThreadId == threadId).OrderBy(m => m.OccurredAt).ToListAsync();
    }

    private async Task<InboxThread> Thread(Guid threadId)
    {
        await using var ctx = _db.CreateContext(null);
        return await ctx.InboxThreads.IgnoreQueryFilters().SingleAsync(t => t.Id == threadId);
    }

    [Fact]
    public async Task A_reply_is_recorded_as_sent_and_threads_with_the_lead()
    {
        var c = await Seed();
        var replyId = (await Messages(c.Thread.Id)).Single(m => m.Direction == MessageDirection.Inbound).RawMessageId;
        var sentId = (await Messages(c.Thread.Id)).First(m => m.Direction == MessageDirection.Outbound).RawMessageId;

        var outcome = await Reply(c.Thread.Id, "  Happy to set up a call.\nDoes Tuesday work?  ");

        Assert.Equal(ReplyOutcomeKind.Sent, outcome.Kind);
        var row = (await Messages(c.Thread.Id)).Last();
        Assert.Equal(outcome.Message!.Id, row.Id);
        Assert.Equal(OutboundDeliveryStatus.Sent, row.DeliveryStatus);
        Assert.Equal(_user, row.SentByUserId);
        Assert.Equal(MessageDirection.Outbound, row.Direction);
        Assert.Equal("Happy to set up a call.\nDoes Tuesday work?", row.TextBody);
        var thread = await Thread(c.Thread.Id);
        Assert.Equal(_clock.Now.UtcDateTime, thread.LastMessageAt);
        Assert.True(thread.IsRead);

        var mail = Assert.Single(_sender.Sent);
        Assert.Equal(c.Lead.Email, mail.To.Address);
        Assert.Equal("Re: Quick question", mail.Subject);
        Assert.Equal($"<{replyId}>", mail.Headers!["In-Reply-To"]);
        Assert.Equal($"<{sentId}> <{replyId}>", mail.Headers!["References"]);
        Assert.True(ReplyAddress.TryDecode(mail.ReplyTo!.Address, _email.InboundDomain, out var enrollmentId));
        Assert.Equal(c.Enrollment.Id, enrollmentId);
        Assert.False(mail.TrackOpens);
        Assert.Null(mail.HtmlBody);
        Assert.DoesNotContain(UnsubscribeFooter.Prompt, mail.TextBody);
        Assert.False(mail.Headers.ContainsKey("List-Unsubscribe"));
        Assert.Equal(row.Id.ToString("N"), mail.CustomArgs!["inboxMessageId"]);
        Assert.Contains("inbox-reply", mail.Categories!);
    }

    [Fact]
    public async Task The_subject_is_not_doubled_when_it_already_starts_with_re()
    {
        var c = await Seed(subject: "RE: Pricing");
        await Reply(c.Thread.Id);
        Assert.Equal("RE: Pricing", Assert.Single(_sender.Sent).Subject);
    }

    [Fact]
    public async Task The_same_request_id_twice_sends_once_and_returns_the_first_message()
    {
        var c = await Seed();
        var requestId = Guid.NewGuid();

        var first = await Reply(c.Thread.Id, requestId: requestId);
        var second = await Reply(c.Thread.Id, "a different body from a retry", requestId: requestId);

        Assert.Equal(ReplyOutcomeKind.Sent, first.Kind);
        Assert.Equal(ReplyOutcomeKind.Duplicate, second.Kind);
        Assert.Equal(first.Message!.Id, second.Message!.Id);
        Assert.Single(_sender.Sent);
        Assert.Single(await Messages(c.Thread.Id), m => m.ClientRequestId == requestId);
    }

    [Fact]
    public async Task Losing_the_double_click_race_at_insert_time_does_not_send()
    {
        var c = await Seed();
        var requestId = Guid.NewGuid();
        await Reply(c.Thread.Id, requestId: requestId);

        // the second request's pre-check ran before the first committed: only the unique index can stop it now
        var outcome = await UseCase(_a, repo => new MissesFirstLookup(repo))
            .ExecuteAsync(_a, _user, c.Thread.Id, new ReplyRequest(requestId, "again"), default);

        Assert.Equal(ReplyOutcomeKind.Duplicate, outcome.Kind);
        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task A_failed_send_is_recorded_and_a_retry_with_a_new_id_succeeds()
    {
        var c = await Seed();
        _sender.Result = SendResult.Fail("550 mailbox unavailable " + new string('x', 600), retryable: false);

        var failed = await Reply(c.Thread.Id);

        Assert.Equal(ReplyOutcomeKind.Failed, failed.Kind);
        var row = (await Messages(c.Thread.Id)).Last();
        Assert.Equal(OutboundDeliveryStatus.Failed, row.DeliveryStatus);
        Assert.StartsWith("550 mailbox unavailable", row.DeliveryError);
        Assert.Equal(SendInboxReplyUseCase.MaxErrorLength, row.DeliveryError!.Length);
        Assert.Equal(T0.AddHours(1), (await Thread(c.Thread.Id)).LastMessageAt);   // a failed send isn't "the latest message"

        _sender.Result = SendResult.Ok("ok");
        var retry = await Reply(c.Thread.Id);

        Assert.Equal(ReplyOutcomeKind.Sent, retry.Kind);
        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task A_sender_exception_is_recorded_as_failed_not_left_pending()
    {
        var c = await Seed();
        var outcome = await new SendInboxReplyUseCase(new InboxRepository(_db.CreateContext(_a)), _suppression, new ThrowingSender(),
            Options.Create(_email), _clock).ExecuteAsync(_a, _user, c.Thread.Id, new ReplyRequest(Guid.NewGuid(), "hi"), default);

        Assert.Equal(ReplyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(OutboundDeliveryStatus.Failed, (await Messages(c.Thread.Id)).Last().DeliveryStatus);
    }

    [Theory]
    [InlineData(LeadStatus.Unsubscribed)]
    [InlineData(LeadStatus.Bounced)]
    public async Task Suppressed_lead_status_blocks_the_reply(LeadStatus status)
    {
        var c = await Seed(status: status);
        var before = (await Messages(c.Thread.Id)).Count;

        var outcome = await Reply(c.Thread.Id);

        Assert.Equal(ReplyOutcomeKind.Blocked, outcome.Kind);
        Assert.Equal(InboxMapping.SuppressedReason, outcome.Error);
        Assert.Empty(_sender.Sent);
        Assert.Equal(before, (await Messages(c.Thread.Id)).Count);
    }

    [Fact]
    public async Task A_suppression_row_blocks_the_reply_even_when_the_status_does_not()
    {
        var c = await Seed();
        _suppression.Add(_a, c.Lead.Email.ToUpperInvariant());

        Assert.Equal(ReplyOutcomeKind.Blocked, (await Reply(c.Thread.Id)).Kind);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task A_deleted_lead_cannot_be_emailed()
    {
        var c = await Seed(deletedAt: T0);
        var outcome = await Reply(c.Thread.Id);
        Assert.Equal(ReplyOutcomeKind.Blocked, outcome.Kind);
        Assert.Equal(InboxMapping.DeletedReason, outcome.Error);
    }

    [Fact]
    public async Task Another_workspace_cannot_reply_on_the_thread()
    {
        var c = await Seed();
        Assert.Equal(ReplyOutcomeKind.NotFound, (await Reply(c.Thread.Id, workspace: _b)).Kind);
        Assert.Empty(_sender.Sent);
    }

    private sealed class ThrowingSender : IEmailSender
    {
        public Task<SendResult> SendAsync(OutboundEmail email, CancellationToken ct = default) => throw new HttpRequestException("connection reset");
    }

    /// <summary>Simulates the race: the first idempotency lookup misses a row another request just committed.</summary>
    private sealed class MissesFirstLookup(IInboxRepository inner) : IInboxRepository
    {
        private bool _missed;

        public Task<InboxMessage?> FindByClientRequestAsync(Guid w, Guid t, Guid r, CancellationToken ct)
        {
            if (_missed) return inner.FindByClientRequestAsync(w, t, r, ct);
            _missed = true;
            return Task.FromResult<InboxMessage?>(null);
        }

        public Task<PagedResult<ThreadListItem>> SearchThreadsAsync(Guid w, ThreadListQuery q, CancellationToken ct) => inner.SearchThreadsAsync(w, q, ct);
        public Task<ThreadListItem?> GetListItemAsync(Guid w, Guid t, CancellationToken ct) => inner.GetListItemAsync(w, t, ct);
        public Task<InboxThreadView?> GetThreadAsync(Guid w, Guid t, CancellationToken ct) => inner.GetThreadAsync(w, t, ct);
        public Task<bool> MarkReadAsync(Guid w, Guid t, bool r, CancellationToken ct) => inner.MarkReadAsync(w, t, r, ct);
        public Task<int> UnreadCountAsync(Guid w, CancellationToken ct) => inner.UnreadCountAsync(w, ct);
        public Task<InboxReplyTarget?> GetThreadForReplyAsync(Guid w, Guid t, CancellationToken ct) => inner.GetThreadForReplyAsync(w, t, ct);
        public Task<string?> FindLatestInboundMessageIdAsync(Guid w, Guid t, CancellationToken ct) => inner.FindLatestInboundMessageIdAsync(w, t, ct);
        public Task<IReadOnlyList<string>> GetReferencesAsync(Guid w, Guid t, CancellationToken ct) => inner.GetReferencesAsync(w, t, ct);
        public Task<Guid?> FindLatestHumanInboundIdAsync(Guid w, Guid t, CancellationToken ct) => inner.FindLatestHumanInboundIdAsync(w, t, ct);
        public Task<bool> TryAddOutboundPendingAsync(InboxMessage m, CancellationToken ct) => inner.TryAddOutboundPendingAsync(m, ct);
        public Task SaveAsync(CancellationToken ct) => inner.SaveAsync(ct);
    }
}
