using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Sending;
using ZenLead.Application.UseCases.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;
using ZenLead.Domain.Templates;
using ZenLead.Infrastructure.Persistence;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Application.Sending;

public class SendDueEmailsUseCaseTests : IDisposable
{
    private readonly SenderHarness _h = new();
    public void Dispose() => _h.Dispose();

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task Due_enrollment_is_sent_once_recorded_and_advanced()
    {
        var campaign = await _h.ActiveCampaignAsync(steps: [Step(0, "Hi {{firstName}}", "Hello {{firstName}} at {{company}}"), Step(3)]);
        var lead = await _h.LeadAsync("Jane Doe");
        var e = await _h.EnrollAsync(campaign, lead);

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Sent);
        var message = Assert.Single(await _h.Messages());
        Assert.Equal(EmailMessageStatus.Sent, message.Status);
        Assert.Equal("sg-123", message.ProviderMessageId);
        Assert.Equal(_h.Now, message.SentAt);
        Assert.Equal("Hi Jane", message.Subject);
        Assert.StartsWith("Hello Jane at Acme", message.Body);
        Assert.False(message.UsedAi);

        var after = await _h.Enrollment(e.Id);
        Assert.Equal(EnrollmentStatus.Active, after.Status);
        Assert.Equal(2, after.NextStepOrder);
        Assert.Equal(SendScheduler.NextSendAt(_h.Now, 3, await _h.WindowOf(campaign)), after.NextSendAt);
        Assert.Equal(_h.Now, after.LastSentAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(lead.Id)).Status);

        // a second run in the same minute sends nothing: the enrollment is no longer due
        Assert.Equal(0, (await _h.RunAsync()).Sent);
        Assert.Single(_h.Sender.Calls);
    }

    [Fact]
    public async Task Email_has_unsubscribe_footer_headers_reply_to_tracking_and_custom_args()
    {
        var (campaign, lead, e) = await _h.DueAsync();

        await _h.RunAsync();

        var email = Assert.Single(_h.Sender.Calls);
        var message = Assert.Single(await _h.Messages());
        var token = _h.Tokens.Create(_h.WorkspaceA, lead.Id);
        Assert.Equal(lead.Email, email.To.Address);
        Assert.Equal(new EmailAddress("outreach@zenrax.test", "Zenrax"), email.From);
        Assert.EndsWith(UnsubscribeFooter.Text($"https://app.zenlead.test/unsubscribe/{token}"), email.TextBody);
        Assert.EndsWith(UnsubscribeFooter.Html($"https://app.zenlead.test/unsubscribe/{token}"), email.HtmlBody);
        Assert.Equal(email.TextBody, message.Body);
        Assert.Equal($"<https://app.zenlead.test/api/v1/unsubscribe/{token}>", email.Headers!["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", email.Headers["List-Unsubscribe-Post"]);
        Assert.True(ReplyAddress.TryDecode(email.ReplyTo!.Address, "reply.zenrax.test", out var enrollmentId));
        Assert.Equal(e.Id, enrollmentId);
        Assert.True(email.TrackOpens);
        Assert.Equal(message.Id.ToString(), email.CustomArgs!["emailMessageId"]);
        Assert.Equal(_h.WorkspaceA.ToString(), email.CustomArgs["workspaceId"]);
        Assert.Equal(campaign.Id.ToString(), email.CustomArgs["campaignId"]);
        Assert.Equal(["campaign"], email.Categories);
    }

    [Fact]
    public async Task Campaign_from_name_overrides_the_configured_one()
    {
        var (campaign, _, _) = await _h.DueAsync();
        await _h.Campaigns.Read(async ctx =>
        {
            (await ctx.Campaigns.IgnoreQueryFilters().SingleAsync(c => c.Id == campaign.Id)).FromName = "Colin at Zenrax";
            return await ctx.SaveChangesAsync();
        });

        await _h.RunAsync();

        Assert.Equal("Colin at Zenrax", Assert.Single(_h.Sender.Calls).From!.Name);
    }

    [Fact]
    public async Task Last_step_completes_the_enrollment_and_the_campaign_once_no_enrollment_is_live()
    {
        var campaign = await _h.ActiveCampaignAsync();
        var a = await _h.EnrollAsync(campaign, await _h.LeadAsync("A One"));
        var b = await _h.EnrollAsync(campaign, await _h.LeadAsync("B Two"), nextSendAt: _h.Now.AddDays(1));

        await _h.RunAsync();

        Assert.Equal(EnrollmentStatus.Completed, (await _h.Enrollment(a.Id)).Status);
        Assert.Null((await _h.Enrollment(a.Id)).NextSendAt);
        Assert.Equal(CampaignStatus.Active, (await _h.Campaigns.CampaignById(campaign.Id)).Status);   // b is still live

        _h.Advance(TimeSpan.FromDays(1));
        await _h.RunAsync();

        Assert.Equal(EnrollmentStatus.Completed, (await _h.Enrollment(b.Id)).Status);
        Assert.Equal(CampaignStatus.Completed, (await _h.Campaigns.CampaignById(campaign.Id)).Status);
    }

    // ---------------------------------------------------------------- time zones, DST and the local day

    [Fact]
    public async Task Follow_up_over_a_spring_forward_gets_the_scheduler_result()
    {
        // US DST starts Sunday 2026-03-08. Saturday 10:00 EST = 15:00 UTC; step 2 one calendar day later at 10:00 EDT = 14:00 UTC.
        _h.SetNow(new DateTime(2026, 3, 7, 15, 0, 0));
        var campaign = await _h.ActiveCampaignAsync(_h.WorkspaceNy, days: SendDays.All, steps: [Step(0), Step(1)]);
        var e = await _h.EnrollAsync(campaign, await _h.LeadAsync(workspace: _h.WorkspaceNy));

        await _h.RunAsync();

        var after = await _h.Enrollment(e.Id);
        Assert.Equal(new DateTime(2026, 3, 8, 14, 0, 0, DateTimeKind.Utc), after.NextSendAt);
        Assert.Equal(SendScheduler.NextSendAt(_h.Now, 1, await _h.WindowOf(campaign)), after.NextSendAt);
    }

    [Fact]
    public async Task Due_is_judged_against_utc_now()
    {
        var campaign = await _h.ActiveCampaignAsync();
        var lead = await _h.LeadAsync();
        await _h.EnrollAsync(campaign, lead, nextSendAt: _h.Now.AddSeconds(30));

        Assert.Equal(0, (await _h.RunAsync()).Sent);
        _h.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, (await _h.RunAsync()).Sent);
    }

    [Theory]
    // Dubai (UTC+4): 19:59 UTC is 23:59 local on the 10th; 20:01 UTC is already the 11th locally (same UTC day)
    [InlineData("A", "2026-03-10T19:59:00", "2026-03-10T19:59:30", "2026-03-10T20:01:00")]
    // New York (EDT, UTC-4): 03:59 UTC on the 11th is 23:59 local on the 10th; 04:01 UTC is the 11th locally
    [InlineData("NY", "2026-03-11T03:59:00", "2026-03-11T03:59:30", "2026-03-11T04:01:00")]
    public async Task Daily_cap_counts_by_the_workspace_local_day(string workspace, string queuedAt, string sameLocalDay, string nextLocalDay)
    {
        var ws = workspace == "A" ? _h.WorkspaceA : _h.WorkspaceNy;
        var campaign = await _h.ActiveCampaignAsync(ws, dailyCap: 1, days: SendDays.All);
        var earlier = await _h.EnrollAsync(campaign, await _h.LeadAsync(workspace: ws), nextSendAt: DateTime.Parse(queuedAt));
        await _h.AddMessageAsync(earlier, campaign.Steps[0].Id, EmailMessageStatus.Sent, DateTime.Parse(queuedAt), DateTime.Parse(queuedAt));

        _h.SetNow(DateTime.Parse(sameLocalDay));
        var capped = await _h.EnrollAsync(campaign, await _h.LeadAsync("Capped Lead", workspace: ws));
        var first = await _h.RunAsync();
        Assert.Equal(0, first.Sent);
        Assert.True(first.CapReached >= 1);
        Assert.True((await _h.Enrollment(capped.Id)).NextSendAt > DateTime.Parse(nextLocalDay));   // waits for the next local day's window

        _h.SetNow(DateTime.Parse(nextLocalDay));
        var fresh = await _h.EnrollAsync(campaign, await _h.LeadAsync("Fresh Lead", workspace: ws));
        var second = await _h.RunAsync();
        Assert.Equal(1, second.Sent);
        Assert.Contains(await _h.Messages(fresh.Id), m => m.Status == EmailMessageStatus.Sent);
    }

    // ---------------------------------------------------------------- idempotency under crash / retry

    [Fact]
    public async Task Crash_between_insert_and_send_is_never_resent_and_becomes_uncertain()
    {
        var (_, _, e) = await _h.DueAsync(steps: [Step(0), Step(2)]);
        _h.Sender.Handler = _ => throw new HttpRequestException("process died");

        var crashed = await _h.RunAsync();
        Assert.Equal(1, crashed.Errors);
        Assert.Equal(EmailMessageStatus.Queued, Assert.Single(await _h.Messages()).Status);

        // a minute later another runner may still be mid-send: leave it alone
        _h.Sender.Handler = _ => Task.FromResult(SendResult.Ok("sg-2"));
        _h.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await _h.RunAsync()).Skipped);

        // once stale, the step is written off: no second send, enrollment moves on
        _h.Advance(TimeSpan.FromMinutes(10));
        await _h.RunAsync();

        Assert.Single(_h.Sender.Calls);
        var message = Assert.Single(await _h.Messages());
        Assert.Equal(EmailMessageStatus.Failed, message.Status);
        Assert.StartsWith("Uncertain", message.Error);
        var after = await _h.Enrollment(e.Id);
        Assert.Equal(2, after.NextStepOrder);
        Assert.Equal(EnrollmentStatus.Active, after.Status);
        Assert.Contains("Warning: ", _h.Logs.All);
        Assert.Contains("uncertain", _h.Logs.All);
    }

    [Fact]
    public async Task Crash_after_send_before_save_does_not_call_the_sender_again()
    {
        var (_, _, e) = await _h.DueAsync(steps: [Step(0), Step(2)]);

        var crashed = await _h.RunAsync(s => new InterceptingStore(s) { FailNextSaves = 1 });
        Assert.Equal(1, crashed.Errors);
        Assert.Equal(EmailMessageStatus.Queued, Assert.Single(await _h.Messages()).Status);
        Assert.Equal(1, (await _h.Enrollment(e.Id)).NextStepOrder);

        _h.Advance(TimeSpan.FromMinutes(11));
        await _h.RunAsync();

        Assert.Single(_h.Sender.Calls);
        Assert.Equal(2, (await _h.Enrollment(e.Id)).NextStepOrder);
    }

    [Fact]
    public async Task Message_already_sent_but_enrollment_not_advanced_is_repaired_without_sending()
    {
        var (campaign, lead, e) = await _h.DueAsync(steps: [Step(0), Step(2)]);
        await _h.AddMessageAsync(e, campaign.Steps[0].Id, EmailMessageStatus.Sent, _h.Now.AddMinutes(-2), _h.Now.AddMinutes(-2));

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Repaired);
        Assert.Empty(_h.Sender.Calls);
        var after = await _h.Enrollment(e.Id);
        Assert.Equal(2, after.NextStepOrder);
        Assert.Equal(SendScheduler.NextSendAt(_h.Now.AddMinutes(-2), 2, await _h.WindowOf(campaign)), after.NextSendAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(lead.Id)).Status);
    }

    [Fact]
    public async Task Overlapping_runs_send_exactly_once()
    {
        await _h.DueAsync();
        var inner = new List<SendRunSummary>();
        var first = true;
        _h.Sender.Handler = async _ =>
        {
            if (first)
            {
                first = false;
                // a second runner starts while the first is inside the provider call
                await using var ctx = _h.Campaigns.Db.CreateContext(null);
                inner.Add(await _h.UseCase(ctx).ExecuteAsync(CancellationToken.None));
            }
            return SendResult.Ok("sg-1");
        };

        var outer = await _h.RunAsync();

        Assert.Single(_h.Sender.Calls);
        Assert.Equal(1, outer.Sent);
        Assert.Equal(0, inner.Single().Sent);
        Assert.Equal(EmailMessageStatus.Sent, Assert.Single(await _h.Messages()).Status);
    }

    [Fact]
    public async Task Losing_the_insert_race_is_a_skip_not_an_exception()
    {
        var (campaign, _, e) = await _h.DueAsync();
        await _h.AddMessageAsync(e, campaign.Steps[0].Id, EmailMessageStatus.Queued, _h.Now);

        // the existence check "missed" the other runner's row: the unique index has the final word
        var summary = await _h.RunAsync(s => new InterceptingStore(s) { HideExistingMessages = true });

        Assert.Equal(0, summary.Errors);
        Assert.Equal(1, summary.Skipped);
        Assert.Empty(_h.Sender.Calls);
    }

    [Fact]
    public async Task TryInsertQueued_returns_false_on_the_unique_index()
    {
        var (campaign, _, e) = await _h.DueAsync();
        EmailMessage Message() => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = e.WorkspaceId, CampaignId = campaign.Id, EnrollmentId = e.Id, StepId = campaign.Steps[0].Id,
            LeadId = e.LeadId, Subject = "s", Body = "b", QueuedAt = _h.Now
        };

        await using var ctx = _h.Campaigns.Db.CreateContext(null);
        var store = new SenderStore(ctx);
        Assert.True(await store.TryInsertQueuedAsync(Message(), CancellationToken.None));
        Assert.False(await store.TryInsertQueuedAsync(Message(), CancellationToken.None));
    }

    // ---------------------------------------------------------------- provider failures

    [Fact]
    public async Task Retryable_failures_back_off_then_fail_the_enrollment_on_the_third()
    {
        var (_, _, e) = await _h.DueAsync();
        _h.Sender.Handler = _ => Task.FromResult(SendResult.Fail("503 Service Unavailable", retryable: true));

        Assert.Equal(1, (await _h.RunAsync()).Retrying);
        var first = await _h.Enrollment(e.Id);
        Assert.Equal(1, first.SendAttempts);
        Assert.Equal(_h.Now.AddMinutes(5), first.NextSendAt);
        Assert.Equal(EnrollmentStatus.Active, first.Status);
        Assert.Empty(await _h.Messages());                                      // no trail for a send that didn't happen

        _h.Advance(TimeSpan.FromMinutes(5));
        await _h.RunAsync();
        var second = await _h.Enrollment(e.Id);
        Assert.Equal(2, second.SendAttempts);
        Assert.Equal(_h.Now.AddMinutes(30), second.NextSendAt);

        _h.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, (await _h.RunAsync()).Failed);
        var third = await _h.Enrollment(e.Id);
        Assert.Equal(EnrollmentStatus.Failed, third.Status);
        Assert.Equal(3, third.SendAttempts);
        Assert.Contains("3 attempts", third.FailureReason);
        Assert.Null(third.NextSendAt);
        Assert.Empty(await _h.Messages());
        Assert.Equal(3, _h.Sender.Calls.Count);
    }

    [Fact]
    public async Task A_successful_send_resets_the_attempt_counter()
    {
        var (_, _, e) = await _h.DueAsync(steps: [Step(0), Step(1)]);
        _h.Sender.Handler = _ => Task.FromResult(SendResult.Fail("timeout", retryable: true));
        await _h.RunAsync();

        _h.Sender.Handler = _ => Task.FromResult(SendResult.Ok("sg"));
        _h.Advance(TimeSpan.FromMinutes(5));
        await _h.RunAsync();

        Assert.Equal(0, (await _h.Enrollment(e.Id)).SendAttempts);
    }

    [Fact]
    public async Task Non_retryable_failure_fails_only_that_enrollment_and_the_batch_continues()
    {
        var campaign = await _h.ActiveCampaignAsync();
        var bad = await _h.LeadAsync("Bad Address");
        var good = await _h.LeadAsync("Good Address");
        var badEnrollment = await _h.EnrollAsync(campaign, bad, _h.Now.AddMinutes(-2));
        var goodEnrollment = await _h.EnrollAsync(campaign, good);
        _h.Sender.Handler = email => Task.FromResult(email.To.Address == bad.Email
            ? SendResult.Fail("400 Bad Request: invalid address", retryable: false)
            : SendResult.Ok("sg-ok"));

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.Failed);
        var failed = await _h.Enrollment(badEnrollment.Id);
        Assert.Equal(EnrollmentStatus.Failed, failed.Status);
        Assert.Contains("400 Bad Request", failed.FailureReason);
        var message = Assert.Single(await _h.Messages(badEnrollment.Id));
        Assert.Equal(EmailMessageStatus.Failed, message.Status);
        Assert.Contains("400", message.Error);
        Assert.Equal(EnrollmentStatus.Completed, (await _h.Enrollment(goodEnrollment.Id)).Status);
    }

    [Fact]
    public async Task An_exception_for_one_enrollment_is_counted_and_does_not_stop_the_loop()
    {
        var campaign = await _h.ActiveCampaignAsync();
        var boom = await _h.LeadAsync("Boom Lead");
        var fine = await _h.LeadAsync("Fine Lead");
        await _h.EnrollAsync(campaign, boom, _h.Now.AddMinutes(-2));
        var fineEnrollment = await _h.EnrollAsync(campaign, fine);
        _h.Sender.Handler = email => email.To.Address == boom.Email
            ? throw new InvalidOperationException("unexpected")
            : Task.FromResult(SendResult.Ok("sg"));

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Sent);
        Assert.Equal(EnrollmentStatus.Completed, (await _h.Enrollment(fineEnrollment.Id)).Status);
    }

    // ---------------------------------------------------------------- caps and fairness

    [Fact]
    public async Task Campaign_daily_cap_stops_sends_for_the_day_and_resets_the_next_local_day()
    {
        var campaign = await _h.ActiveCampaignAsync(dailyCap: 2);
        var enrollments = new List<CampaignEnrollment>();
        for (var i = 0; i < 3; i++) enrollments.Add(await _h.EnrollAsync(campaign, await _h.LeadAsync($"Lead {i}"), _h.Now.AddMinutes(-3 + i)));

        var today = await _h.RunAsync();
        Assert.Equal(2, today.Sent);
        Assert.Equal(1, today.CapReached);
        var waiting = await _h.Enrollment(enrollments[2].Id);
        Assert.Equal(new DateTime(2026, 3, 12, 5, 0, 0, DateTimeKind.Utc), waiting.NextSendAt);   // Thursday 09:00 Dubai

        Assert.Equal(0, (await _h.RunAsync()).Sent);                         // still today: nothing more

        _h.SetNow(new DateTime(2026, 3, 12, 5, 0, 0));
        Assert.Equal(1, (await _h.RunAsync()).Sent);
    }

    [Fact]
    public async Task Workspace_warm_up_cap_applies_across_campaigns()
    {
        _h.Sending.WorkspaceDailyCap = 3;
        var c1 = await _h.ActiveCampaignAsync();
        var c2 = await _h.ActiveCampaignAsync();
        for (var i = 0; i < 2; i++)
        {
            await _h.EnrollAsync(c1, await _h.LeadAsync($"One {i}"));
            await _h.EnrollAsync(c2, await _h.LeadAsync($"Two {i}"));
        }

        var summary = await _h.RunAsync();

        Assert.Equal(3, summary.Sent);
        Assert.Equal(1, summary.CapReached);
    }

    [Fact]
    public async Task Per_run_throttle_does_not_starve_another_workspace()
    {
        _h.Sending.WorkspaceDailyCap = 1000;
        var busy = await _h.ActiveCampaignAsync(_h.WorkspaceA, dailyCap: 500);
        for (var i = 0; i < 100; i++) await _h.EnrollAsync(busy, await _h.LeadAsync($"Busy {i}"), _h.Now.AddMinutes(-200 + i));
        var quiet = await _h.ActiveCampaignAsync(_h.WorkspaceB, dailyCap: 500, days: SendDays.All);
        var quietEnrollments = new List<CampaignEnrollment>();
        for (var i = 0; i < 3; i++) quietEnrollments.Add(await _h.EnrollAsync(quiet, await _h.LeadAsync($"Quiet {i}", _h.WorkspaceB)));

        var summary = await _h.RunAsync();

        Assert.Equal(23, summary.Sent);                                       // 20 (A's throttle) + all 3 of B
        Assert.Equal(80, summary.Throttled);
        foreach (var e in quietEnrollments) Assert.Equal(EnrollmentStatus.Completed, (await _h.Enrollment(e.Id)).Status);
    }

    [Fact]
    public async Task Due_list_is_interleaved_so_a_busy_workspace_cannot_fill_the_batch()
    {
        _h.Sending.BatchSize = 4;
        var busy = await _h.ActiveCampaignAsync(_h.WorkspaceA);
        for (var i = 0; i < 10; i++) await _h.EnrollAsync(busy, await _h.LeadAsync($"Busy {i}"), _h.Now.AddHours(-5).AddMinutes(i));
        var quiet = await _h.ActiveCampaignAsync(_h.WorkspaceB, days: SendDays.All);
        await _h.EnrollAsync(quiet, await _h.LeadAsync("Quiet", _h.WorkspaceB));

        await using var ctx = _h.Campaigns.Db.CreateContext(null);
        var due = await new SenderStore(ctx).GetDueAsync(_h.Now, 4, CancellationToken.None);

        Assert.Equal(4, due.Count);
        Assert.Contains(due, d => d.WorkspaceId == _h.WorkspaceB);
    }

    // ---------------------------------------------------------------- suppression and stop conditions

    [Theory]
    [InlineData(LeadStatus.Unsubscribed, EnrollmentStatus.Unsubscribed)]
    [InlineData(LeadStatus.Bounced, EnrollmentStatus.Bounced)]
    [InlineData(LeadStatus.Replied, EnrollmentStatus.Replied)]
    public async Task Lead_status_changed_after_enrollment_stops_without_sending(LeadStatus leadStatus, EnrollmentStatus expected)
    {
        var (_, lead, e) = await _h.DueAsync();
        await _h.UpdateLeadAsync(lead.Id, l => l.Status = leadStatus);

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Stopped);
        Assert.Empty(_h.Sender.Calls);
        var after = await _h.Enrollment(e.Id);
        Assert.Equal(expected, after.Status);
        Assert.Null(after.NextSendAt);
    }

    [Fact]
    public async Task Address_on_the_suppression_list_is_not_sent_even_if_the_lead_is_new()
    {
        var (_, lead, e) = await _h.DueAsync();
        await _h.SuppressAsync(_h.WorkspaceA, lead.Email.ToUpperInvariant());

        await _h.RunAsync();

        Assert.Empty(_h.Sender.Calls);
        Assert.Equal(EnrollmentStatus.Unsubscribed, (await _h.Enrollment(e.Id)).Status);
    }

    [Fact]
    public async Task Another_workspaces_suppression_does_not_apply()
    {
        var (_, lead, _) = await _h.DueAsync();
        await _h.SuppressAsync(_h.WorkspaceB, lead.Email);

        Assert.Equal(1, (await _h.RunAsync()).Sent);
    }

    [Fact]
    public async Task Invalid_email_fails_the_enrollment()
    {
        var (_, lead, e) = await _h.DueAsync();
        await _h.UpdateLeadAsync(lead.Id, l => l.EmailVerificationStatus = EmailVerificationStatus.Invalid);

        await _h.RunAsync();

        Assert.Empty(_h.Sender.Calls);
        var after = await _h.Enrollment(e.Id);
        Assert.Equal(EnrollmentStatus.Failed, after.Status);
        Assert.Contains("invalid", after.FailureReason);
    }

    [Fact]
    public async Task Restricted_mode_with_an_empty_allow_list_blocks_everyone()
    {
        _h.Sending.RecipientAllowList = [];
        var (_, _, e) = await _h.DueAsync();

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Blocked);
        Assert.Empty(_h.Sender.Calls);
        var after = await _h.Enrollment(e.Id);
        Assert.Equal(EnrollmentStatus.Failed, after.Status);
        Assert.Contains("allow-list", after.FailureReason);
    }

    [Fact]
    public async Task Live_mode_ignores_the_allow_list()
    {
        _h.Sending.RecipientAllowList = [];
        _h.Sending.Mode = SendingMode.Live;
        await _h.DueAsync();

        Assert.Equal(1, (await _h.RunAsync()).Sent);
    }

    [Theory]
    [InlineData("example.com", "jane@example.com", true)]
    [InlineData("EXAMPLE.com", "Jane@Example.COM", true)]
    [InlineData("@example.com", "jane@example.com", true)]
    [InlineData("jane@example.com", "JANE@example.com", true)]
    [InlineData("jane@example.com", "john@example.com", false)]
    [InlineData("example.com", "jane@sub.example.com", false)]
    [InlineData("example.com", "jane@example.com.evil.test", false)]
    [InlineData("", "jane@example.com", false)]
    public void Allow_list_matches_addresses_and_domains_case_insensitively(string entry, string email, bool allowed)
        => Assert.Equal(allowed, new SendingOptions { RecipientAllowList = [entry] }.Allows(email));

    [Fact]
    public void Restricted_is_the_default_and_an_empty_list_allows_nobody()
    {
        var o = new SendingOptions();
        Assert.Equal(SendingMode.Restricted, o.Mode);
        Assert.False(o.Allows("anyone@example.com"));
        Assert.True(new SendingOptions { Mode = SendingMode.Live }.Allows("anyone@example.com"));
    }

    [Fact]
    public async Task Paused_campaign_is_never_sent()
    {
        var (campaign, _, _) = await _h.DueAsync();
        await _h.Campaigns.Read(async ctx =>
        {
            (await ctx.Campaigns.IgnoreQueryFilters().SingleAsync(c => c.Id == campaign.Id)).Status = CampaignStatus.Paused;
            return await ctx.SaveChangesAsync();
        });

        Assert.Equal(0, (await _h.RunAsync()).Total);
        Assert.Empty(_h.Sender.Calls);
    }

    [Fact]
    public async Task Enrollment_of_a_soft_deleted_lead_is_stopped()
    {
        var (_, lead, e) = await _h.DueAsync();
        await _h.UpdateLeadAsync(lead.Id, l => l.DeletedAt = DateTime.UtcNow);

        await _h.RunAsync();

        Assert.Empty(_h.Sender.Calls);
        var after = await _h.Enrollment(e.Id);
        Assert.Equal(EnrollmentStatus.Failed, after.Status);
        Assert.Contains("deleted", after.FailureReason);
    }

    // ---------------------------------------------------------------- content and AI

    [Fact]
    public async Task Missing_token_fails_naming_it_and_other_enrollments_are_unaffected()
    {
        var campaign = await _h.ActiveCampaignAsync(steps: [Step(0, "Hi", "About {{company}}")]);
        var noCompany = await _h.LeadAsync("No Company", company: null);
        var withCompany = await _h.LeadAsync("Has Company");
        var broken = await _h.EnrollAsync(campaign, noCompany, _h.Now.AddMinutes(-2));
        await _h.EnrollAsync(campaign, withCompany);

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Sent);
        var after = await _h.Enrollment(broken.Id);
        Assert.Equal(EnrollmentStatus.Failed, after.Status);
        Assert.Contains("{{company}}", after.FailureReason);
    }

    [Fact]
    public async Task Fallback_token_value_is_used()
    {
        var campaign = await _h.ActiveCampaignAsync(steps: [Step(0, "Hello {{title|friend}}", "Body")]);
        await _h.EnrollAsync(campaign, await _h.LeadAsync());

        await _h.RunAsync();

        Assert.Equal("Hello friend", Assert.Single(_h.Sender.Calls).Subject);
    }

    [Fact]
    public async Task Ai_personalisation_is_used_and_logged_with_purpose_campaign_and_enrollment()
    {
        var campaign = await _h.ActiveCampaignAsync(ai: true);
        var lead = await _h.LeadAsync();
        var e = await _h.EnrollAsync(campaign, lead);

        await _h.RunAsync();

        Assert.Equal(1, _h.Composer.Calls);
        Assert.Contains("Hello Jane", _h.Composer.LastContext!.AdditionalContext);
        var message = Assert.Single(await _h.Messages());
        Assert.True(message.UsedAi);
        Assert.Equal("AI subject", message.Subject);
        Assert.StartsWith("AI body", message.Body);
        var usage = Assert.Single(await _h.UsageRows());
        Assert.Equal(AiPurpose.Personalise, usage.Purpose);
        Assert.Equal(campaign.Id, usage.CampaignId);
        Assert.Equal(e.Id, usage.EnrollmentId);
        Assert.True(usage.Succeeded);
        Assert.Equal(100, usage.PromptTokens);
        Assert.Equal(_h.Pricing.EstimateCostUsd(100, 50), usage.EstimatedCostUsd);
    }

    public static TheoryData<string> AiFailures => ["provider", "billed", "timeout", "unexpected"];

    [Theory]
    [MemberData(nameof(AiFailures))]
    public async Task Ai_failure_sends_the_template_unchanged(string failure)
    {
        var campaign = await _h.ActiveCampaignAsync(ai: true, steps: [Step(0, "Hi {{firstName}}", "Hello {{firstName}}")]);
        await _h.EnrollAsync(campaign, await _h.LeadAsync());
        _h.Composer.Handler = _ => failure switch
        {
            "provider" => throw new AiProviderException(AiProviderFailureKind.Unavailable, "down"),
            "billed" => throw new AiProviderException(AiProviderFailureKind.InvalidResponse, "junk", promptTokens: 80, completionTokens: 20),
            "timeout" => throw new TaskCanceledException("HttpClient timeout"),
            _ => throw new InvalidOperationException("bug")
        };

        var summary = await _h.RunAsync();

        Assert.Equal(1, summary.Sent);
        var message = Assert.Single(await _h.Messages());
        Assert.False(message.UsedAi);
        Assert.Equal("Hi Jane", message.Subject);
        Assert.StartsWith("Hello Jane", message.Body);
        var usage = await _h.UsageRows();
        if (failure == "billed")
        {
            var row = Assert.Single(usage);
            Assert.False(row.Succeeded);
            Assert.Equal(AiPurpose.Personalise, row.Purpose);
        }
        else Assert.Empty(usage);
    }

    [Fact]
    public async Task Exhausted_ai_budget_skips_the_model_and_sends_the_template()
    {
        _h.Budget.State = BudgetState.Exceeded;
        var campaign = await _h.ActiveCampaignAsync(ai: true, steps: [Step(0, "Hi {{firstName}}", "Hello")]);
        await _h.EnrollAsync(campaign, await _h.LeadAsync());

        await _h.RunAsync();

        Assert.Equal(0, _h.Composer.Calls);
        Assert.Equal("Hi Jane", Assert.Single(_h.Sender.Calls).Subject);
    }

    [Fact]
    public async Task Budget_warning_still_personalises()
    {
        _h.Budget.State = BudgetState.Warning;
        var campaign = await _h.ActiveCampaignAsync(ai: true);
        await _h.EnrollAsync(campaign, await _h.LeadAsync());

        await _h.RunAsync();

        Assert.Equal(1, _h.Composer.Calls);
    }

    [Fact]
    public async Task Footer_is_appended_after_ai_so_the_model_cannot_drop_or_alter_it()
    {
        _h.Composer.Handler = _ => new ComposedEmail("Hi", "Click https://evil.test/unsubscribe to stop.", 10);
        var campaign = await _h.ActiveCampaignAsync(ai: true);
        var lead = await _h.LeadAsync();
        await _h.EnrollAsync(campaign, lead);

        await _h.RunAsync();

        var email = Assert.Single(_h.Sender.Calls);
        var url = $"https://app.zenlead.test/unsubscribe/{_h.Tokens.Create(_h.WorkspaceA, lead.Id)}";
        Assert.EndsWith(UnsubscribeFooter.Text(url), email.TextBody);
        Assert.EndsWith(UnsubscribeFooter.Html(url), email.HtmlBody);
    }

    [Fact]
    public async Task Ai_subject_with_line_breaks_is_flattened()
    {
        _h.Composer.Handler = _ => new ComposedEmail("Quick idea\r\nBcc: victim@x.test", "Body", 10);
        var campaign = await _h.ActiveCampaignAsync(ai: true);
        await _h.EnrollAsync(campaign, await _h.LeadAsync());

        await _h.RunAsync();

        Assert.Equal("Quick idea Bcc: victim@x.test", Assert.Single(_h.Sender.Calls).Subject);
    }

    [Fact]
    public void Flatten_caps_subjects_at_300_chars()
        => Assert.Equal(300, SendDueEmailsUseCase.FlattenSubject(new string('a', 400)).Length);

    [Fact]
    public void Html_twin_encodes_the_text()
        => Assert.Equal("<p>a &lt;b&gt;<br>c</p><p>d</p>", SendDueEmailsUseCase.HtmlFromText("a <b>\nc\n\nd"));
}
