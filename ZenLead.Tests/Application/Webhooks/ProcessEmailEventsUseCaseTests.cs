using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ZenLead.Application.UseCases.Webhooks;
using ZenLead.Application.Webhooks;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Application.Campaigns;
using ZenLead.Tests.Application.Sending;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Application.Webhooks;

/// <summary>
/// F28: <see cref="ProcessEmailEventsUseCase"/> over the real <see cref="EmailEventStore"/> on in-memory SQLite. Each
/// <see cref="ProcessAsync"/> is a fresh context with no current workspace, exactly like one anonymous webhook request.
/// </summary>
public sealed class ProcessEmailEventsUseCaseTests : IDisposable
{
    private readonly SenderHarness _h = new();
    private TestDb Db => _h.Campaigns.Db;
    private DateTime Now => _h.Now;

    public void Dispose() => _h.Dispose();

    private sealed record Seeded(Campaign Campaign, Lead Lead, CampaignEnrollment Enrollment, EmailMessage Message);

    /// <summary>A lead in a 2-step campaign whose step-1 email was sent an hour ago (enrollment waiting for step 2).</summary>
    private async Task<Seeded> SentAsync(Guid? workspace = null, Lead? lead = null)
    {
        var campaign = await _h.ActiveCampaignAsync(workspace, steps: [CampaignHarness.Step(), CampaignHarness.Step(3, "Follow up", "Again")]);
        lead ??= await _h.LeadAsync(workspace: workspace, status: LeadStatus.Contacted);
        var enrollment = await _h.EnrollAsync(campaign, lead, nextSendAt: Now.AddDays(3), nextStepOrder: 2);
        var message = new EmailMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = campaign.WorkspaceId, CampaignId = campaign.Id, EnrollmentId = enrollment.Id,
            StepId = campaign.Steps.Single(s => s.Order == 1).Id, LeadId = lead.Id, Subject = "Hi", Body = "Hello",
            Status = EmailMessageStatus.Sent, QueuedAt = Now.AddHours(-1), SentAt = Now.AddHours(-1), ProviderMessageId = "pm-1"
        };
        await using var ctx = Db.CreateContext(null);
        ctx.EmailMessages.Add(message);
        await ctx.SaveChangesAsync();
        return new Seeded(campaign, lead, enrollment, message);
    }

    private static List<RawEmailEvent> Parse(params Dictionary<string, object?>[] events)
        => JsonSerializer.Deserialize<List<RawEmailEvent>>(EventFixtures.Body(events), JsonSerializerOptions.Web)!;   // as the controller does

    private Dictionary<string, object?> Ev(string type, Seeded s, int minutes = 0, string? id = null, string? bounceType = null,
        string? reason = null, string? status = null, Guid? workspace = null, Guid? messageId = null)
        => EventFixtures.Event(type, messageId ?? s.Message.Id, workspace ?? s.Message.WorkspaceId, Now.AddMinutes(minutes), id,
            s.Lead.Email, bounceType, reason, status);

    private async Task<EventBatchResult> ProcessAsync(IReadOnlyList<RawEmailEvent> events, params IInterceptor[] interceptors)
    {
        await using var ctx = Db.CreateContext(null, interceptors);
        var useCase = new ProcessEmailEventsUseCase(new EmailEventStore(ctx, _h.Clock), _h.Clock, NullLogger<ProcessEmailEventsUseCase>.Instance);
        return await useCase.ExecuteAsync(events, CancellationToken.None);
    }

    private Task<EmailMessage> Message(Guid id) => _h.Campaigns.Read(c => c.EmailMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(m => m.Id == id));
    private Task<List<SuppressedEmail>> Suppressions() => _h.Campaigns.Read(c => c.SuppressedEmails.IgnoreQueryFilters().AsNoTracking().ToListAsync());
    private Task<int> ProcessedCount() => _h.Campaigns.Read(c => c.ProcessedWebhookEvents.CountAsync());

    [Fact]
    public async Task Delivered_and_open_update_the_message()
    {
        var s = await SentAsync();
        var result = await ProcessAsync(Parse(Ev("delivered", s, 1), Ev("open", s, 30)));

        Assert.Equal(2, result.Applied);
        var m = await Message(s.Message.Id);
        Assert.Equal(EmailMessageStatus.Delivered, m.Status);
        Assert.Equal(Now.AddMinutes(1), m.DeliveredAt);
        Assert.Equal(Now.AddMinutes(30), m.OpenedAt);
        Assert.Equal("14c5d75ce93.dfd.64b469@ismtpd-555", m.SmtpMessageId);
    }

    [Fact]
    public async Task The_same_batch_twice_is_all_duplicates_and_changes_nothing()
    {
        var s = await SentAsync();
        var batch = Parse(Ev("delivered", s, 1, "e-1"), Ev("open", s, 30, "e-2"));
        await ProcessAsync(batch);
        var before = await Message(s.Message.Id);

        var second = await ProcessAsync(Parse(Ev("delivered", s, 1, "e-1"), Ev("open", s, 30, "e-2")));

        Assert.Equal(2, second.Duplicates);
        Assert.Equal(0, second.Applied);
        var after = await Message(s.Message.Id);
        Assert.Equal((before.Status, before.DeliveredAt, before.OpenedAt), (after.Status, after.DeliveredAt, after.OpenedAt));
        Assert.Equal(2, await ProcessedCount());
    }

    [Fact]
    public async Task Duplicates_within_one_batch_are_applied_once()
    {
        var s = await SentAsync();
        var result = await ProcessAsync(Parse(Ev("open", s, 30, "same"), Ev("open", s, 30, "same")));
        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Duplicates);
    }

    [Fact]
    public async Task Events_without_an_sg_event_id_get_a_stable_synthetic_id()
    {
        var s = await SentAsync();
        var e = Ev("open", s, 30);
        e.Remove("sg_event_id");
        Assert.Equal(1, (await ProcessAsync(Parse(e))).Applied);
        Assert.Equal(1, (await ProcessAsync(Parse(e))).Duplicates);
    }

    [Fact]
    public async Task Out_of_order_across_batches_ends_in_the_same_state_as_in_order()
    {
        var a = await SentAsync();
        await ProcessAsync(Parse(Ev("open", a, 30)));
        await ProcessAsync(Parse(Ev("delivered", a, 1)));

        var b = await SentAsync();
        await ProcessAsync(Parse(Ev("delivered", b, 1)));
        await ProcessAsync(Parse(Ev("open", b, 30)));

        var ma = await Message(a.Message.Id);
        var mb = await Message(b.Message.Id);
        Assert.Equal((ma.Status, ma.DeliveredAt, ma.OpenedAt), (mb.Status, mb.DeliveredAt, mb.OpenedAt));
        Assert.Equal(Now.AddMinutes(1), ma.DeliveredAt);
    }

    [Fact]
    public async Task Hard_bounce_stops_the_enrollment_marks_the_lead_suppresses_and_blocks_later_sends()
    {
        var s = await SentAsync();
        // the same lead is also due in another campaign: the bounce must keep it from ever being emailed again
        var other = await _h.ActiveCampaignAsync();
        var otherEnrollment = await _h.EnrollAsync(other, s.Lead);

        await ProcessAsync(Parse(Ev("bounce", s, 2, bounceType: "bounce", reason: "550 5.1.1 The email account does not exist", status: "5.1.1")));

        var m = await Message(s.Message.Id);
        Assert.Equal(EmailMessageStatus.Bounced, m.Status);
        Assert.NotNull(m.BouncedAt);
        Assert.Equal(EnrollmentStatus.Bounced, (await _h.Enrollment(s.Enrollment.Id)).Status);
        Assert.Null((await _h.Enrollment(s.Enrollment.Id)).NextSendAt);
        Assert.Equal(LeadStatus.Bounced, (await _h.Lead(s.Lead.Id)).Status);
        var row = Assert.Single(await Suppressions());
        Assert.Equal((s.Lead.Email, SuppressionReason.Bounced, s.Lead.Id), (row.Email, row.Reason, row.SourceLeadId));

        await _h.RunAsync();                                                   // F23 sender: suppressed at send time
        Assert.Empty(_h.Sender.Calls);
        Assert.Empty(await _h.Messages(otherEnrollment.Id));
    }

    [Fact]
    public async Task Soft_stop_fails_the_enrollment_without_suppressing()
    {
        var s = await SentAsync();
        await ProcessAsync(Parse(Ev("bounce", s, 2, bounceType: "blocked", reason: "IP address on a blocklist", status: "5.7.1")));

        var e = await _h.Enrollment(s.Enrollment.Id);
        Assert.Equal(EnrollmentStatus.Failed, e.Status);
        Assert.Equal("Blocked/dropped: IP address on a blocklist", e.FailureReason);
        Assert.Null(e.NextSendAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.Lead.Id)).Status);
        Assert.Empty(await Suppressions());
    }

    [Fact]
    public async Task Dropped_for_a_suppressed_address_is_a_hard_bounce()
    {
        var s = await SentAsync();
        await ProcessAsync(Parse(Ev("dropped", s, 1, reason: "Bounced Address")));
        Assert.Equal(EmailMessageStatus.Dropped, (await Message(s.Message.Id)).Status);
        Assert.Equal(LeadStatus.Bounced, (await _h.Lead(s.Lead.Id)).Status);
        Assert.Single(await Suppressions());
    }

    [Fact]
    public async Task Spam_report_unsubscribes_the_lead_suppresses_and_stops_every_campaign()
    {
        var s = await SentAsync();
        var other = await _h.ActiveCampaignAsync();
        var otherEnrollment = await _h.EnrollAsync(other, s.Lead, nextSendAt: Now.AddDays(1));

        await ProcessAsync(Parse(Ev("spamreport", s, 5)));

        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.Lead.Id)).Status);
        Assert.Equal(EnrollmentStatus.Unsubscribed, (await _h.Enrollment(s.Enrollment.Id)).Status);
        Assert.Equal(EnrollmentStatus.Unsubscribed, (await _h.Enrollment(otherEnrollment.Id)).Status);
        Assert.Equal(SuppressionReason.SpamReport, Assert.Single(await Suppressions()).Reason);
    }

    [Fact]
    public async Task Unsubscribe_event_acts_like_the_unsubscribe_flow()
    {
        var s = await SentAsync();
        await ProcessAsync(Parse(Ev("group_unsubscribe", s, 5)));
        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.Lead.Id)).Status);
        Assert.Equal(SuppressionReason.Unsubscribed, Assert.Single(await Suppressions()).Reason);
    }

    [Fact]
    public async Task Bounce_and_spam_report_in_one_batch_suppress_once()
    {
        var s = await SentAsync();
        await ProcessAsync(Parse(Ev("bounce", s, 1, status: "5.1.1"), Ev("spamreport", s, 2)));
        Assert.Equal(SuppressionReason.Bounced, Assert.Single(await Suppressions()).Reason);   // first reason wins
    }

    [Fact]
    public async Task Unknown_message_and_mismatched_workspace_are_ignored_and_change_nothing()
    {
        var s = await SentAsync();
        var result = await ProcessAsync(Parse(
            Ev("delivered", s, 1, messageId: Guid.NewGuid()),
            Ev("bounce", s, 2, status: "5.1.1", workspace: Guid.NewGuid())));

        Assert.Equal(2, result.UnknownMessage);
        Assert.Equal(0, result.Applied);
        var m = await Message(s.Message.Id);
        Assert.Equal(EmailMessageStatus.Sent, m.Status);
        Assert.Null(m.DeliveredAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.Lead.Id)).Status);
        Assert.Empty(await Suppressions());
    }

    [Fact]
    public async Task Cross_workspace_event_carrying_B_id_and_A_message_changes_nothing()
    {
        var a = await SentAsync(_h.WorkspaceA);
        await SentAsync(_h.WorkspaceB);                                        // B is a real workspace with its own data

        var result = await ProcessAsync(Parse(Ev("spamreport", a, 1, workspace: _h.WorkspaceB), Ev("open", a, 2, workspace: _h.WorkspaceB)));

        Assert.Equal(2, result.UnknownMessage);
        Assert.Null((await Message(a.Message.Id)).OpenedAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(a.Lead.Id)).Status);
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(a.Enrollment.Id)).Status);
        Assert.Empty(await Suppressions());
    }

    [Fact]
    public async Task Malformed_custom_args_make_the_event_unknown_not_the_batch_fail()
    {
        var s = await SentAsync();
        var bad = Ev("open", s, 1);
        bad["emailMessageId"] = "not-a-guid";
        var result = await ProcessAsync(Parse(bad, Ev("delivered", s, 2)));
        Assert.Equal(1, result.UnknownMessage);
        Assert.Equal(1, result.Applied);
    }

    [Fact]
    public async Task Ignored_event_types_do_not_fail_the_batch()
    {
        var s = await SentAsync();
        var result = await ProcessAsync(Parse(Ev("processed", s, 0), Ev("deferred", s, 1), Ev("click", s, 2), Ev("delivered", s, 3)));
        Assert.Equal(3, result.Ignored);
        Assert.Equal(1, result.Applied);
    }

    [Fact]
    public async Task A_batch_of_500_events_takes_a_bounded_number_of_queries()
    {
        var seeded = new List<Seeded>();
        for (var i = 0; i < 25; i++) seeded.Add(await SentAsync());
        var events = new List<Dictionary<string, object?>>();
        for (var i = 0; i < 500; i++)
        {
            var s = seeded[i % seeded.Count];
            events.Add(i % 50 == 0 ? Ev("bounce", s, i, status: "5.1.1") : Ev(i % 2 == 0 ? "delivered" : "open", s, i));
        }

        var counter = new CommandCounter();
        var result = await ProcessAsync(Parse(events.ToArray()), counter);

        Assert.Equal(500, result.Applied);
        Assert.True(counter.Count <= 8, $"{counter.Count} queries for 500 events");
    }

    [Fact]
    public async Task A_failure_mid_batch_rolls_back_effects_and_processed_ids_so_the_retry_applies_cleanly()
    {
        var s = await SentAsync();
        var batch = Parse(Ev("delivered", s, 1, "r-1"), Ev("bounce", s, 2, "r-2", status: "5.1.1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(batch, new FailingSave()));

        Assert.Equal(0, await ProcessedCount());
        Assert.Equal(EmailMessageStatus.Sent, (await Message(s.Message.Id)).Status);
        Assert.Empty(await Suppressions());
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.Lead.Id)).Status);

        var retry = await ProcessAsync(Parse(Ev("delivered", s, 1, "r-1"), Ev("bounce", s, 2, "r-2", status: "5.1.1")));
        Assert.Equal(2, retry.Applied);
        Assert.Equal(EmailMessageStatus.Bounced, (await Message(s.Message.Id)).Status);
        Assert.Single(await Suppressions());
    }

    /// <summary>Counts reads (SELECTs); the single commit's batched writes are not "queries".</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated crash during commit");
    }
}
