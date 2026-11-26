using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Application.Sending;

/// <summary>F25: every campaign send creates/attaches to the enrollment's inbox thread, in the same save as the enrollment advance.</summary>
public class OutboundThreadingTests : IDisposable
{
    private readonly SenderHarness _h = new();
    public void Dispose() => _h.Dispose();

    private Task<List<InboxThread>> Threads() => _h.Campaigns.Read(c => c.InboxThreads.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    private Task<List<InboxMessage>> InboxMessages() => _h.Campaigns.Read(c => c.InboxMessages.IgnoreQueryFilters().AsNoTracking().OrderBy(m => m.OccurredAt).ToListAsync());

    [Fact]
    public async Task A_send_creates_the_thread_and_an_outbound_message()
    {
        var (campaign, lead, e) = await _h.DueAsync(steps: [Step(0, "Hi {{firstName}}", "Hello {{firstName}}"), Step(2)]);

        await _h.RunAsync();

        var sent = Assert.Single(await _h.Messages());
        var thread = Assert.Single(await Threads());
        Assert.Equal(e.Id, thread.EnrollmentId);
        Assert.Equal(lead.Id, thread.LeadId);
        Assert.Equal(campaign.Id, thread.CampaignId);
        Assert.Equal(_h.WorkspaceA, thread.WorkspaceId);
        Assert.Equal("Hi Jane", thread.Subject);
        Assert.True(thread.IsRead);                                                         // nothing unread until a reply arrives
        Assert.Equal(_h.Now, thread.LastMessageAt);

        var outbound = Assert.Single(await InboxMessages());
        Assert.Equal(MessageDirection.Outbound, outbound.Direction);
        Assert.Equal(thread.Id, outbound.ThreadId);
        Assert.Equal(sent.Id, outbound.EmailMessageId);
        Assert.Equal(_h.Email.FromAddress, outbound.FromAddress);
        Assert.Equal(lead.Email, outbound.ToAddress);
        Assert.Equal(sent.Subject, outbound.Subject);
        Assert.Equal(sent.Body, outbound.TextBody);
        Assert.Equal(sent.SentAt, outbound.OccurredAt);
        Assert.False(outbound.IsAutoReply);
    }

    [Fact]
    public async Task The_second_step_reuses_the_thread()
    {
        var (_, _, e) = await _h.DueAsync(steps: [Step(0, "First subject", "b1"), Step(2, "Second subject", "b2")]);
        await _h.RunAsync();
        _h.SetNow((await _h.Enrollment(e.Id)).NextSendAt!.Value);

        await _h.RunAsync();

        Assert.Equal(2, _h.Sender.Calls.Count);
        var thread = Assert.Single(await Threads());
        Assert.Equal("First subject", thread.Subject);                                      // the first outbound subject stays
        Assert.Equal(_h.Now, thread.LastMessageAt);
        var messages = await InboxMessages();
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal(thread.Id, m.ThreadId));
        Assert.Equal((await _h.Messages()).Select(m => (Guid?)m.Id).ToHashSet(), messages.Select(m => m.EmailMessageId).ToHashSet());
    }

    [Fact]
    public async Task A_failed_save_commits_neither_the_thread_nor_the_enrollment_advance()
    {
        var (_, _, e) = await _h.DueAsync(steps: [Step(0), Step(2)]);

        var crashed = await _h.RunAsync(s => new InterceptingStore(s) { FailNextSaves = 1 });

        Assert.Equal(1, crashed.Errors);
        Assert.Empty(await Threads());
        Assert.Empty(await InboxMessages());
        Assert.Equal(1, (await _h.Enrollment(e.Id)).NextStepOrder);
        Assert.Equal(EmailMessageStatus.Queued, Assert.Single(await _h.Messages()).Status);
    }

    [Fact]
    public async Task A_repaired_send_is_attached_once()
    {
        var (campaign, _, e) = await _h.DueAsync(steps: [Step(0), Step(2)]);
        await _h.AddMessageAsync(e, campaign.Steps[0].Id, EmailMessageStatus.Sent, _h.Now.AddMinutes(-2), _h.Now.AddMinutes(-2));

        Assert.Equal(1, (await _h.RunAsync()).Repaired);

        var outbound = Assert.Single(await InboxMessages());
        Assert.Equal((await _h.Messages()).Single().Id, outbound.EmailMessageId);
    }

    [Fact]
    public async Task Failed_sends_create_no_thread()
    {
        await _h.DueAsync();
        _h.Sender.Handler = _ => Task.FromResult(ZenLead.Application.Abstractions.SendResult.Fail("rejected", retryable: false));

        await _h.RunAsync();

        Assert.Empty(await Threads());
    }

    [Fact]
    public async Task The_reply_to_the_sender_builds_decodes_to_the_enrollment_for_the_configured_inbound_domain()
    {
        var (_, _, e) = await _h.DueAsync();

        await _h.RunAsync();

        var replyTo = Assert.Single(_h.Sender.Calls).ReplyTo!.Address;
        Assert.True(ReplyAddress.TryDecode(replyTo, _h.Email.InboundDomain, out var decoded));
        Assert.Equal(e.Id, decoded);
    }
}
