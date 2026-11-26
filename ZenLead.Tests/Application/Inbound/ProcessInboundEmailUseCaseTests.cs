using Microsoft.EntityFrameworkCore;
using ZenLead.Application.UseCases.Inbound;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Inbound;

public class ProcessInboundEmailUseCaseTests : IDisposable
{
    private readonly InboundHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static Dictionary<string, string> Headers(params (string Key, string Value)[] items)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Date"] = "Wed, 11 Mar 2026 10:15:02 +0400" };
        foreach (var (k, v) in items) d[k] = v;
        return d;
    }

    // ---------------- matching ----------------

    [Fact]
    public async Task Token_in_to_attaches_to_the_enrollment_thread()
    {
        var s = await _h.SeedAsync();

        var result = await _h.ProcessAsync(_h.ReplyFrom(s));

        Assert.Equal(InboundOutcome.Attached, result.Outcome);
        Assert.Equal(ProcessInboundEmailUseCase.ViaToken, result.Via);
        var thread = Assert.Single(await _h.Threads());
        Assert.Equal(s.EnrollmentId, thread.EnrollmentId);
        Assert.Equal(s.LeadId, thread.LeadId);
        Assert.Equal(s.CampaignId, thread.CampaignId);
        var message = Assert.Single(await _h.Messages());
        Assert.Equal(thread.Id, message.ThreadId);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("Yes, let's talk.", message.TextBody);                                  // quoted history stripped
        Assert.Contains("wrote:", message.FullTextBody);                                    // full text kept
        Assert.Equal("msg-1@mail.example.com", message.RawMessageId);
        Assert.Equal("dkim=@example.com : pass; spf=pass", message.AuthSummary);
        Assert.False(message.SenderMismatch);
        Assert.Empty(await _h.Quarantine());
    }

    [Fact]
    public async Task Token_only_in_the_envelope_recipients_still_attaches()
    {
        var s = await _h.SeedAsync();
        var mail = InboundHarness.Mail(s.LeadEmail, ["business@zenraxconsulting.test", s.ReplyTo]);    // visible To differs; envelope carries the token

        var result = await _h.ProcessAsync(mail);

        Assert.Equal(ProcessInboundEmailUseCase.ViaToken, result.Via);
    }

    [Theory]
    [InlineData("r-not-a-guid@reply.zenlead.test")]
    [InlineData("r-0f8fad5bd9cb469fa16570867728950e@other.domain.test")]
    [InlineData("0f8fad5bd9cb469fa16570867728950e@reply.zenlead.test")]
    public async Task Wrong_or_malformed_token_falls_through_to_the_headers(string to)
    {
        var s = await _h.SeedAsync(providerMessageId: "W8GaiuHRSYqCwOeYOb1D4A");
        var mail = InboundHarness.Mail(s.LeadEmail, [to], inReplyTo: "W8GaiuHRSYqCwOeYOb1D4A.filterdrecv-5bd4-1-63C9-2@ismtpd0001p1lon1.sendgrid.net");

        var result = await _h.ProcessAsync(mail);

        Assert.Equal(ProcessInboundEmailUseCase.ViaHeaders, result.Via);
        Assert.Equal(s.EnrollmentId, (await _h.Threads()).Single().EnrollmentId);
    }

    [Fact]
    public async Task References_with_the_provider_id_as_local_part_match_level_2()
    {
        var s = await _h.SeedAsync(providerMessageId: "W8GaiuHRSYqCwOeYOb1D4A");
        var mail = InboundHarness.Mail(s.LeadEmail, ["business@zenraxconsulting.test"],
            references: ["older@mail.example.com", "W8GaiuHRSYqCwOeYOb1D4A@geopod-ismtpd-3"]);

        Assert.Equal(ProcessInboundEmailUseCase.ViaHeaders, (await _h.ProcessAsync(mail)).Via);
    }

    [Fact]
    public async Task Sender_email_fallback_attaches_when_exactly_one_workspace_recently_emailed_them()
    {
        var s = await _h.SeedAsync(email: "jane@acme.test");
        var mail = InboundHarness.Mail("Jane@Acme.test", ["business@zenraxconsulting.test"], dkim: null, spf: null);

        var result = await _h.ProcessAsync(mail);

        Assert.Equal(InboundOutcome.Attached, result.Outcome);
        Assert.Equal(ProcessInboundEmailUseCase.ViaSenderEmail, result.Via);
        Assert.Equal(s.EnrollmentId, (await _h.Threads()).Single().EnrollmentId);
    }

    [Fact]
    public async Task Sender_email_fallback_picks_the_latest_send_within_the_workspace()
    {
        var older = await _h.SeedAsync(email: "jane@acme.test", sentDaysAgo: 20);
        await _h.Read(async c =>
        {
            // a second campaign for the same lead, emailed more recently
            var lead = await c.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == older.LeadId);
            var step = new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, Order = 1, SubjectTemplate = "s", BodyTemplate = "b" };
            var campaign = new Campaign { Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, Name = "Summer", Status = CampaignStatus.Active, CreatedAt = _h.Now, Steps = [step] };
            var e = new CampaignEnrollment { Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, CampaignId = campaign.Id, LeadId = lead.Id, EnrolledAt = _h.Now };
            c.AddRange(campaign, e, new EmailMessage
            {
                Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, CampaignId = campaign.Id, EnrollmentId = e.Id, StepId = step.Id, LeadId = lead.Id,
                Subject = "s", Body = "b", Status = EmailMessageStatus.Sent, QueuedAt = _h.Now.AddDays(-2), SentAt = _h.Now.AddDays(-2)
            });
            await c.SaveChangesAsync();
            return e.Id;
        });

        var result = await _h.ProcessAsync(InboundHarness.Mail("jane@acme.test", ["business@zenraxconsulting.test"]));

        Assert.Equal(InboundOutcome.Attached, result.Outcome);
        Assert.NotEqual(older.EnrollmentId, (await _h.Threads()).Single().EnrollmentId);
    }

    [Fact]
    public async Task Two_workspaces_that_both_emailed_the_sender_are_quarantined_as_ambiguous_never_guessed()
    {
        var a = await _h.SeedAsync(_h.WorkspaceA, "jane@acme.test");
        var b = await _h.SeedAsync(_h.WorkspaceB, "jane@acme.test");

        var result = await _h.ProcessAsync(InboundHarness.Mail("jane@acme.test", ["business@zenraxconsulting.test"]));

        Assert.Equal(InboundOutcome.Quarantined, result.Outcome);
        Assert.Equal(InboundQuarantine.Ambiguous, Assert.Single(await _h.Quarantine()).Reason);
        Assert.Empty(await _h.Messages());
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(a.LeadId)).Status);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(b.LeadId)).Status);
    }

    [Fact]
    public async Task No_candidate_is_quarantined_as_no_match_with_headers_but_no_body()
    {
        var mail = InboundHarness.Mail("stranger@elsewhere.test", ["random@reply.zenlead.test"], text: "SECRET BODY CONTENT",
            headers: Headers(("Subject", "Hello"), ("X-Mailer", "TestMail 1.0")));

        var result = await _h.ProcessAsync(mail);

        Assert.Equal(InboundOutcome.Quarantined, result.Outcome);
        var q = Assert.Single(await _h.Quarantine());
        Assert.Equal(InboundQuarantine.NoMatch, q.Reason);
        Assert.Equal("stranger@elsewhere.test", q.FromAddress);
        Assert.Equal("random@reply.zenlead.test", q.ToAddress);
        Assert.Contains("X-Mailer: TestMail 1.0", q.HeadersSnippet);
        Assert.DoesNotContain("SECRET BODY CONTENT", q.HeadersSnippet);
        Assert.Empty(await _h.Messages());
        Assert.Empty(await _h.Threads());
    }

    [Fact]
    public async Task Sender_email_match_ignores_sends_older_than_the_window()
    {
        await _h.SeedAsync(email: "jane@acme.test", sentDaysAgo: 61);

        var result = await _h.ProcessAsync(InboundHarness.Mail("jane@acme.test", ["business@zenraxconsulting.test"]));

        Assert.Equal(InboundQuarantine.NoMatch, result.Reason);
    }

    [Fact]
    public async Task Token_for_an_enrollment_that_does_not_exist_is_not_attached()
    {
        var mail = InboundHarness.Mail("jane@acme.test", [$"r-{Guid.NewGuid():N}@reply.zenlead.test"]);

        var result = await _h.ProcessAsync(mail);

        Assert.Equal(InboundOutcome.Quarantined, result.Outcome);
        Assert.Empty(await _h.Messages());
    }

    // ---------------- spoofing ----------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("none", "fail")]
    [InlineData("{@attacker.test : pass}", "softfail")]                    // signed, but not by the From domain
    [InlineData("{@example.com : fail}", "neutral")]
    public async Task A_different_unauthenticated_sender_using_a_token_is_quarantined_as_spoof(string? dkim, string? spf)
    {
        var s = await _h.SeedAsync();

        var result = await _h.ProcessAsync(_h.ReplyFrom(s, from: "ceo@example.com", dkim: dkim, spf: spf));

        Assert.Equal(InboundQuarantine.SpoofSuspected, result.Reason);
        Assert.Empty(await _h.Messages());
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.LeadId)).Status);
    }

    [Theory]
    [InlineData("{@example.com : pass}", null)]
    [InlineData("{@mail.example.com : fail, @example.com : pass}", "fail")]
    [InlineData(null, "pass")]
    public async Task A_different_but_authenticated_sender_is_attached_and_flagged(string? dkim, string? spf)
    {
        var s = await _h.SeedAsync();

        var result = await _h.ProcessAsync(_h.ReplyFrom(s, from: "assistant@example.com", dkim: dkim, spf: spf));

        Assert.Equal(InboundOutcome.Attached, result.Outcome);
        Assert.True(Assert.Single(await _h.Messages()).SenderMismatch);
    }

    [Theory]
    [InlineData("{@example.com : pass}", "assistant@example.com", true)]
    [InlineData("{@example.com : pass}", "assistant@eu.example.com", true)]       // subdomain of the signing domain
    [InlineData("{@example.com : pass}", "assistant@notexample.com", false)]
    [InlineData("{@gmail.com : pass, @sendgrid.net : pass}", "x@example.com", false)]
    [InlineData("pass", "x@example.com", true)]
    [InlineData("", "x@example.com", false)]
    public void Dkim_only_counts_for_the_from_domain(string dkim, string from, bool expected)
        => Assert.Equal(expected, ProcessInboundEmailUseCase.DkimPasses(dkim, from));

    // ---------------- idempotency ----------------

    [Fact]
    public async Task The_same_message_id_delivered_twice_is_stored_once_and_has_effect_once()
    {
        var s = await _h.SeedAsync();
        Assert.Equal(InboundOutcome.Attached, (await _h.ProcessAsync(_h.ReplyFrom(s))).Outcome);
        // make any second application visible
        await _h.Read(async c =>
        {
            (await c.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == s.LeadId)).Status = LeadStatus.Contacted;
            var e = await c.CampaignEnrollments.IgnoreQueryFilters().SingleAsync(x => x.Id == s.EnrollmentId);
            e.Status = EnrollmentStatus.Active;
            var t = await c.InboxThreads.IgnoreQueryFilters().SingleAsync();
            t.IsRead = true;
            return await c.SaveChangesAsync();
        });

        var second = await _h.ProcessAsync(_h.ReplyFrom(s));

        Assert.Equal(InboundOutcome.Duplicate, second.Outcome);
        Assert.Single(await _h.Messages());
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.True((await _h.Threads()).Single().IsRead);                                  // the duplicate changed nothing at all
    }

    [Fact]
    public async Task Mail_without_a_message_id_still_dedupes_on_a_synthetic_id()
    {
        var s = await _h.SeedAsync();

        var first = await _h.ProcessAsync(_h.ReplyFrom(s, messageId: null));
        var second = await _h.ProcessAsync(_h.ReplyFrom(s, messageId: null) with { ReceivedAt = new DateTime(2026, 3, 11, 7, 0, 0, DateTimeKind.Utc) });

        Assert.Equal(InboundOutcome.Attached, first.Outcome);
        Assert.Equal(InboundOutcome.Duplicate, second.Outcome);
        Assert.StartsWith("synthetic:", Assert.Single(await _h.Messages()).RawMessageId);
    }

    [Fact]
    public async Task Two_different_replies_in_one_thread_are_both_stored()
    {
        var s = await _h.SeedAsync();

        await _h.ProcessAsync(_h.ReplyFrom(s, messageId: "one@x"));
        await _h.ProcessAsync(_h.ReplyFrom(s, messageId: "two@x") with { ReceivedAt = new DateTime(2026, 3, 12, 6, 0, 0, DateTimeKind.Utc) });

        var thread = Assert.Single(await _h.Threads());
        Assert.Equal(2, (await _h.Messages()).Count(m => m.ThreadId == thread.Id));
        Assert.Equal(new DateTime(2026, 3, 12, 6, 0, 0), thread.LastMessageAt);
    }

    [Fact]
    public async Task The_same_message_id_in_another_workspace_is_not_a_duplicate()
    {
        var a = await _h.SeedAsync(_h.WorkspaceA);
        var b = await _h.SeedAsync(_h.WorkspaceB);

        Assert.Equal(InboundOutcome.Attached, (await _h.ProcessAsync(_h.ReplyFrom(a, messageId: "same@x"))).Outcome);
        Assert.Equal(InboundOutcome.Attached, (await _h.ProcessAsync(_h.ReplyFrom(b, messageId: "same@x"))).Outcome);
    }

    // ---------------- effects ----------------

    [Fact]
    public async Task A_human_reply_marks_the_lead_replied_stops_the_enrollment_and_leaves_the_thread_unread()
    {
        var s = await _h.SeedAsync();

        await _h.ProcessAsync(_h.ReplyFrom(s));

        var lead = await _h.Lead(s.LeadId);
        Assert.Equal(LeadStatus.Replied, lead.Status);
        Assert.Equal(_h.Now, lead.UpdatedAt);
        var e = await _h.Enrollment(s.EnrollmentId);
        Assert.Equal(EnrollmentStatus.Replied, e.Status);
        Assert.Null(e.NextSendAt);
        var thread = (await _h.Threads()).Single();
        Assert.False(thread.IsRead);
        Assert.Equal(ReplyClassification.Unclassified, thread.Classification);
        Assert.Equal(ReplyClassification.Unclassified, (await _h.Messages()).Single().Classification);
    }

    [Fact]
    public async Task An_out_of_office_reply_is_flagged_and_the_sequence_continues()
    {
        var s = await _h.SeedAsync();
        var mail = _h.ReplyFrom(s, headers: Headers(("Auto-Submitted", "auto-replied")), subject: "Automatic reply: Quick question");

        await _h.ProcessAsync(mail);

        var message = (await _h.Messages()).Single();
        Assert.True(message.IsAutoReply);
        Assert.Equal(ReplyClassification.OutOfOffice, message.Classification);
        var e = await _h.Enrollment(s.EnrollmentId);
        Assert.Equal(EnrollmentStatus.Active, e.Status);
        Assert.NotNull(e.NextSendAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.LeadId)).Status);
        var thread = (await _h.Threads()).Single();
        Assert.Equal(ReplyClassification.OutOfOffice, thread.Classification);
        Assert.False(thread.IsRead);
    }

    [Fact]
    public async Task A_bounce_report_is_classified_bounce_and_does_not_stop_the_sequence()
    {
        var s = await _h.SeedAsync();
        var mail = _h.ReplyFrom(s, from: "mailer-daemon@googlemail.com", dkim: "{@googlemail.com : pass}",
            headers: Headers(("Content-Type", "multipart/report; report-type=delivery-status; boundary=b1"), ("Return-Path", "<>")),
            subject: "Delivery Status Notification (Failure)");

        var result = await _h.ProcessAsync(mail);

        Assert.Equal(InboundOutcome.Attached, result.Outcome);
        var message = (await _h.Messages()).Single();
        Assert.Equal(ReplyClassification.Bounce, message.Classification);
        Assert.True(message.SenderMismatch);
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.LeadId)).Status);
    }

    [Fact]
    public async Task A_reply_to_a_completed_enrollment_marks_the_lead_but_keeps_the_final_status()
    {
        var s = await _h.SeedAsync(enrollmentStatus: EnrollmentStatus.Completed);

        await _h.ProcessAsync(_h.ReplyFrom(s));

        Assert.Equal(LeadStatus.Replied, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(EnrollmentStatus.Completed, (await _h.Enrollment(s.EnrollmentId)).Status);
    }

    [Fact]
    public async Task A_reply_from_an_unsubscribed_lead_is_stored_without_a_status_regression()
    {
        var s = await _h.SeedAsync(leadStatus: LeadStatus.Unsubscribed, enrollmentStatus: EnrollmentStatus.Unsubscribed);

        var result = await _h.ProcessAsync(_h.ReplyFrom(s));

        Assert.Equal(InboundOutcome.Attached, result.Outcome);
        Assert.Single(await _h.Messages());
        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(EnrollmentStatus.Unsubscribed, (await _h.Enrollment(s.EnrollmentId)).Status);
    }

    [Fact]
    public async Task A_paused_enrollment_is_stopped_too()
    {
        var s = await _h.SeedAsync(enrollmentStatus: EnrollmentStatus.Paused);

        await _h.ProcessAsync(_h.ReplyFrom(s));

        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(s.EnrollmentId)).Status);
    }

    [Fact]
    public async Task Html_only_mail_gets_a_text_body_and_sanitised_html()
    {
        var s = await _h.SeedAsync();
        var mail = _h.ReplyFrom(s) with { Text = null, Html = "<div>Sounds <b>good</b> &amp; thanks<br>Jane</div><style>p{}</style>" };

        await _h.ProcessAsync(mail);

        var message = (await _h.Messages()).Single();
        Assert.Equal("Sounds good & thanks\nJane", message.TextBody);
        Assert.StartsWith("[sanitized]", message.HtmlBody);
        Assert.Equal(1, _h.Sanitizer.Calls);
    }

    [Fact]
    public async Task A_reply_reuses_the_thread_the_outbound_send_created()
    {
        var s = await _h.SeedAsync();
        var existing = await _h.Read(async c =>
        {
            var t = new InboxThread
            {
                Id = Guid.NewGuid(), WorkspaceId = s.WorkspaceId, LeadId = s.LeadId, CampaignId = s.CampaignId, EnrollmentId = s.EnrollmentId,
                Subject = "Quick question", LastMessageAt = _h.Now.AddDays(-1), IsRead = true, CreatedAt = _h.Now.AddDays(-1)
            };
            c.InboxThreads.Add(t);
            await c.SaveChangesAsync();
            return t.Id;
        });

        await _h.ProcessAsync(_h.ReplyFrom(s, subject: "Re: Quick question"));

        var thread = Assert.Single(await _h.Threads());
        Assert.Equal(existing, thread.Id);
        Assert.Equal("Quick question", thread.Subject);                                     // replies keep the first outbound subject
    }

    // ---------------- cross-workspace (priority) ----------------

    [Fact]
    public async Task A_token_for_workspace_a_only_ever_writes_into_workspace_a()
    {
        var a = await _h.SeedAsync(_h.WorkspaceA, "jane@acme.test");
        var b = await _h.SeedAsync(_h.WorkspaceB, "jane@acme.test");                       // same person, another tenant

        await _h.ProcessAsync(_h.ReplyFrom(a));

        Assert.All(await _h.Threads(), t => Assert.Equal(_h.WorkspaceA, t.WorkspaceId));
        Assert.All(await _h.Messages(), m => Assert.Equal(_h.WorkspaceA, m.WorkspaceId));
        Assert.Equal(LeadStatus.Replied, (await _h.Lead(a.LeadId)).Status);
        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(a.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(b.LeadId)).Status);
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(b.EnrollmentId)).Status);
    }

    [Fact]
    public async Task Tenant_queries_see_inbox_rows_only_in_their_own_workspace()
    {
        var a = await _h.SeedAsync(_h.WorkspaceA);
        await _h.ProcessAsync(_h.ReplyFrom(a));

        await using var asB = _h.Db.CreateContext(_h.WorkspaceB);
        Assert.Empty(await asB.InboxThreads.ToListAsync());
        Assert.Empty(await asB.InboxMessages.ToListAsync());
        await using var asA = _h.Db.CreateContext(_h.WorkspaceA);
        Assert.Single(await asA.InboxThreads.ToListAsync());
    }

    // ---------------- logging ----------------

    [Fact]
    public async Task Logs_contain_ids_but_no_addresses_or_body_text()
    {
        var s = await _h.SeedAsync(email: "jane.private@acme.test");
        await _h.ProcessAsync(InboundHarness.Mail("jane.private@acme.test", [s.ReplyTo], text: "My PRIVATE reply text"));
        await _h.ProcessAsync(InboundHarness.Mail("nobody@else.test", ["x@reply.zenlead.test"], text: "Other PRIVATE text"));

        var logs = _h.Logs.All;
        Assert.Contains(s.EnrollmentId.ToString(), logs);
        Assert.DoesNotContain("jane.private@acme.test", logs);
        Assert.DoesNotContain("nobody@else.test", logs);
        Assert.DoesNotContain("PRIVATE", logs);
        Assert.DoesNotContain(s.ReplyTo, logs);
    }

    // ---------------- helpers ----------------

    [Fact]
    public void Ids_from_headers_include_local_parts_and_the_segment_before_the_first_dot()
    {
        var mail = InboundHarness.Mail("a@b.c", ["x@y.z"], inReplyTo: "<abc.def.ghi@host.example>", references: ["ref1@host", "<plain>"]);

        var ids = ProcessInboundEmailUseCase.IdsFrom(mail);

        Assert.Equal(["abc.def.ghi@host.example", "abc.def.ghi", "abc", "ref1@host", "ref1", "plain"], ids);
    }
}
