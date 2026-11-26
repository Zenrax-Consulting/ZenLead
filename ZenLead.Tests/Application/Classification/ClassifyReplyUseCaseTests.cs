using ZenLead.Application.Abstractions;
using ZenLead.Application.Classification;
using ZenLead.Application.Sending;
using ZenLead.Domain.Enums;
using ZenLead.Domain.Scheduling;

namespace ZenLead.Tests.Application.Classification;

public class ClassifyReplyUseCaseTests : IDisposable
{
    private readonly ClassificationHarness _h = new();
    public void Dispose() => _h.Dispose();

    // ---------------- labels without consequences ----------------

    [Fact]
    public async Task Interested_saves_the_label_and_thread_chip_and_changes_nothing_else()
    {
        var s = await _h.SeedAsync();
        _h.Classifier.Returns(ReplyClassification.Interested, 0.95m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var m = await _h.Message(s.MessageId);
        Assert.Equal(ReplyClassification.Interested, m.Classification);
        Assert.Equal(0.95m, m.ClassificationConfidence);
        Assert.Equal(ClassificationSource.Ai, m.ClassifiedBy);
        Assert.False(m.NeedsReview);
        Assert.Null(m.ClassificationNote);
        Assert.Equal(ReplyClassification.Interested, (await _h.Thread(s.ThreadId)).Classification);
        Assert.Equal(LeadStatus.Replied, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(s.OtherEnrollmentId)).Status);
        Assert.Empty(await _h.Suppressions());
    }

    [Fact]
    public async Task Classifier_gets_the_cleaned_reply_and_the_original_email()
    {
        var s = await _h.SeedAsync(replyText: "Yes please");

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var input = Assert.Single(_h.Classifier.Inputs);
        Assert.Equal("Yes please", input.ReplyText);
        Assert.Equal("Quick question", input.OriginalSubject);
        Assert.Equal("Hello Jane, would a short call make sense?", input.OriginalBody);
    }

    [Fact]
    public async Task Long_reply_is_truncated_before_it_reaches_the_classifier()
    {
        var s = await _h.SeedAsync(replyText: new string('a', 50_000));

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Equal(_h.Options.MaxReplyChars, Assert.Single(_h.Classifier.Inputs).ReplyText.Length);
    }

    [Fact]
    public async Task Low_confidence_harmless_label_is_recorded_without_review()
    {
        var s = await _h.SeedAsync();
        _h.Classifier.Returns(ReplyClassification.NotInterested, 0.4m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var m = await _h.Message(s.MessageId);
        Assert.Equal(ReplyClassification.NotInterested, m.Classification);
        Assert.False(m.NeedsReview);                                         // not consequential
        Assert.Equal(ClassificationNotes.LowConfidence, m.ClassificationNote);
    }

    // ---------------- unsubscribe ----------------

    [Fact]
    public async Task Confident_unsubscribe_suppresses_and_stops_every_campaign_but_leaves_the_own_enrollment_Replied()
    {
        var s = await _h.SeedAsync(replyText: "Please take me off your list.");
        _h.Classifier.Returns(ReplyClassification.Unsubscribe, 0.9m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.LeadId)).Status);
        var row = Assert.Single(await _h.Suppressions());
        Assert.Equal(s.LeadEmail, row.Email);
        Assert.Equal(s.WorkspaceId, row.WorkspaceId);
        Assert.Equal(SuppressionReason.Unsubscribed, row.Reason);
        var other = await _h.Enrollment(s.OtherEnrollmentId);
        Assert.Equal(EnrollmentStatus.Unsubscribed, other.Status);           // the other campaign is stopped too
        Assert.Null(other.NextSendAt);
        // F25 already stopped this one as Replied; ApplyStop only changes live enrollments — it is stopped either way. Don't "fix" this.
        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.False((await _h.Message(s.MessageId)).NeedsReview);
    }

    [Fact]
    public async Task Low_confidence_unsubscribe_with_a_keyword_is_raised_to_the_threshold_and_acts()
    {
        var s = await _h.SeedAsync(replyText: "Not for us. Please remove me from your list.");
        _h.Classifier.Returns(ReplyClassification.Unsubscribe, 0.5m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var m = await _h.Message(s.MessageId);
        Assert.Equal(_h.Options.AutoActionMinConfidence, m.ClassificationConfidence);
        Assert.False(m.NeedsReview);
        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.LeadId)).Status);
        Assert.Single(await _h.Suppressions());
    }

    [Fact]
    public async Task Low_confidence_unsubscribe_without_a_keyword_takes_no_action_and_needs_review()
    {
        var s = await _h.SeedAsync(replyText: "Hmm, I'd rather not hear about this again, maybe.");
        _h.Classifier.Returns(ReplyClassification.Unsubscribe, 0.5m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var m = await _h.Message(s.MessageId);
        Assert.Equal(ReplyClassification.Unsubscribe, m.Classification);
        Assert.Equal(0.5m, m.ClassificationConfidence);
        Assert.True(m.NeedsReview);
        Assert.Equal(ClassificationNotes.LowConfidence, m.ClassificationNote);
        Assert.Equal(LeadStatus.Replied, (await _h.Lead(s.LeadId)).Status);
        Assert.Empty(await _h.Suppressions());
        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(s.OtherEnrollmentId)).Status);
    }

    [Fact]
    public async Task Keyword_never_invents_an_unsubscribe_the_model_did_not_suggest()
    {
        var s = await _h.SeedAsync(replyText: "Interested! But please remove me from the CC list and only email me.");
        _h.Classifier.Returns(ReplyClassification.Interested, 0.6m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Equal(ReplyClassification.Interested, (await _h.Message(s.MessageId)).Classification);
        Assert.Equal(LeadStatus.Replied, (await _h.Lead(s.LeadId)).Status);
        Assert.Empty(await _h.Suppressions());
    }

    // ---------------- out of office ----------------

    [Fact]
    public async Task Confident_out_of_office_resumes_the_sequence_after_the_delay_and_reverts_the_lead()
    {
        var s = await _h.SeedAsync(replyText: "I'm travelling until the 20th with limited access to email.");
        _h.Classifier.Returns(ReplyClassification.OutOfOffice, 0.9m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var e = await _h.Enrollment(s.EnrollmentId);
        var window = new SendWindow(TimeZoneInfo.Utc, new TimeOnly(9, 0), new TimeOnly(17, 0), SendDays.Weekdays);   // campaign defaults, workspace UTC
        Assert.Equal(EnrollmentStatus.Active, e.Status);
        Assert.Equal(SendScheduler.NextSendAt(_h.Now, 7, window), e.NextSendAt);
        Assert.Equal(new DateTime(2026, 3, 18, 9, 0, 0, DateTimeKind.Utc), e.NextSendAt);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(ReplyClassification.OutOfOffice, (await _h.Thread(s.ThreadId)).Classification);
    }

    [Fact]
    public async Task Out_of_office_does_not_resume_when_the_thread_has_another_human_reply()
    {
        var s = await _h.SeedAsync(replyText: "Away until Monday.");
        await _h.AddReplyAsync(s, "Actually, let's talk next week.", _h.Now.AddHours(-2), classification: ReplyClassification.Interested,
            classifiedBy: ClassificationSource.Ai);
        _h.Classifier.Returns(ReplyClassification.OutOfOffice, 0.95m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Replied, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(ReplyClassification.OutOfOffice, (await _h.Message(s.MessageId)).Classification);
    }

    [Fact]
    public async Task Low_confidence_out_of_office_does_not_resume_and_needs_review()
    {
        var s = await _h.SeedAsync(replyText: "Back later.");
        _h.Classifier.Returns(ReplyClassification.OutOfOffice, 0.4m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.True((await _h.Message(s.MessageId)).NeedsReview);
        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Replied, (await _h.Lead(s.LeadId)).Status);
    }

    [Fact]
    public async Task Late_classification_of_an_older_reply_does_not_overwrite_the_thread_chip()
    {
        var s = await _h.SeedAsync(replyText: "Out until Friday.");
        await _h.AddReplyAsync(s, "Send me pricing.", _h.Now.AddMinutes(-1));   // newer, not classified yet
        _h.Classifier.Returns(ReplyClassification.OutOfOffice, 0.95m);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Equal(ReplyClassification.Unclassified, (await _h.Thread(s.ThreadId)).Classification);
    }

    // ---------------- failures ----------------

    [Fact]
    public async Task Invalid_model_output_leaves_the_reply_unclassified_logs_the_billed_tokens_and_does_not_throw()
    {
        var s = await _h.SeedAsync();
        _h.Classifier.Throw = new AiProviderException(AiProviderFailureKind.InvalidResponse, "bad", promptTokens: 150, completionTokens: 9);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var m = await _h.Message(s.MessageId);
        Assert.Equal(ReplyClassification.Unclassified, m.Classification);
        Assert.Equal(ClassificationNotes.InvalidOutput, m.ClassificationNote);
        Assert.Equal(ClassificationSource.None, m.ClassifiedBy);
        var usage = Assert.Single(_h.Usage.Entries);
        Assert.False(usage.Succeeded);
        Assert.Equal(150, usage.PromptTokens);
        Assert.Equal(9, usage.CompletionTokens);
        Assert.Equal(AiPurpose.ClassifyReply, usage.Purpose);
    }

    [Theory]
    [InlineData(AiProviderFailureKind.RateLimited)]
    [InlineData(AiProviderFailureKind.Unavailable)]
    public async Task Transient_provider_errors_propagate_for_Hangfire_to_retry_and_change_nothing(AiProviderFailureKind kind)
    {
        var s = await _h.SeedAsync();
        _h.Classifier.Throw = new AiProviderException(kind, "transient");

        await Assert.ThrowsAsync<AiProviderException>(() => _h.ClassifyAsync(s.WorkspaceId, s.MessageId));

        var m = await _h.Message(s.MessageId);
        Assert.Equal(ReplyClassification.Unclassified, m.Classification);
        Assert.Null(m.ClassificationNote);
        Assert.Equal(ClassificationSource.None, m.ClassifiedBy);
        Assert.Empty(_h.Usage.Entries);
    }

    [Fact]
    public async Task Exceeded_budget_skips_the_classifier_and_leaves_the_reply_unclassified()
    {
        var s = await _h.SeedAsync();
        _h.Budget.State = BudgetState.Exceeded;

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Empty(_h.Classifier.Inputs);
        Assert.Empty(_h.Usage.Entries);
        var m = await _h.Message(s.MessageId);
        Assert.Equal(ReplyClassification.Unclassified, m.Classification);
        Assert.Equal(ClassificationNotes.Budget, m.ClassificationNote);
        Assert.Equal(s.ThreadId, m.ThreadId);                                   // still in the inbox
    }

    [Fact]
    public async Task Warning_budget_still_classifies()
    {
        var s = await _h.SeedAsync();
        _h.Budget.State = BudgetState.Warning;

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Single(_h.Classifier.Inputs);
    }

    // ---------------- idempotency & scope ----------------

    [Fact]
    public async Task Already_classified_message_is_a_no_op()
    {
        var s = await _h.SeedAsync(classifiedBy: ClassificationSource.Manual, classification: ReplyClassification.NotInterested);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Empty(_h.Classifier.Inputs);
        Assert.Equal(ReplyClassification.NotInterested, (await _h.Message(s.MessageId)).Classification);
    }

    [Fact]
    public async Task Auto_reply_is_a_no_op()
    {
        var s = await _h.SeedAsync(isAutoReply: true, classifiedBy: ClassificationSource.Header, classification: ReplyClassification.OutOfOffice);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Empty(_h.Classifier.Inputs);
        Assert.Equal(0, _h.Budget.Checks);
    }

    [Fact]
    public async Task Running_twice_classifies_once()
    {
        var s = await _h.SeedAsync();

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);
        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        Assert.Single(_h.Classifier.Inputs);
        Assert.Single(_h.Usage.Entries);
    }

    [Fact]
    public async Task Another_workspaces_message_id_loads_nothing_and_does_nothing()
    {
        var a = await _h.SeedAsync(workspace: _h.WorkspaceA);
        _h.Classifier.Returns(ReplyClassification.Unsubscribe, 1m);

        await _h.ClassifyAsync(_h.WorkspaceB, a.MessageId);

        Assert.Empty(_h.Classifier.Inputs);
        Assert.Equal(0, _h.Budget.Checks);
        Assert.Equal(ClassificationSource.None, (await _h.Message(a.MessageId)).ClassifiedBy);
        Assert.Empty(await _h.Suppressions());
    }

    [Fact]
    public async Task Effects_with_another_workspace_id_do_nothing()
    {
        var a = await _h.SeedAsync(workspace: _h.WorkspaceA);

        await _h.ApplyAsync(_h.WorkspaceB, a.MessageId, ReplyClassification.Unsubscribe, 1m, manual: true);

        Assert.Equal(LeadStatus.Replied, (await _h.Lead(a.LeadId)).Status);
        Assert.Empty(await _h.Suppressions());
    }

    [Fact]
    public async Task Unknown_message_is_a_no_op()
    {
        await _h.ClassifyAsync(_h.WorkspaceA, Guid.NewGuid());

        Assert.Empty(_h.Classifier.Inputs);
    }

    // ---------------- usage logging ----------------

    [Fact]
    public async Task Usage_row_is_written_with_purpose_model_ids_and_the_per_model_price()
    {
        var s = await _h.SeedAsync();
        _h.Classifier.Returns(ReplyClassification.Interested, 0.9m, prompt: 1000, completion: 100);

        await _h.ClassifyAsync(s.WorkspaceId, s.MessageId);

        var row = Assert.Single(_h.Usage.Entries);
        Assert.Equal(AiPurpose.ClassifyReply, row.Purpose);
        Assert.Equal(s.WorkspaceId, row.WorkspaceId);
        Assert.Equal("gpt-4o-mini", row.Model);
        Assert.Equal(s.CampaignId, row.CampaignId);
        Assert.Equal(s.EnrollmentId, row.EnrollmentId);
        Assert.True(row.Succeeded);
        Assert.Equal(1000, row.PromptTokens);
        Assert.Equal(100, row.CompletionTokens);
        Assert.Equal(1000 / 1000m * 0.00015m + 100 / 1000m * 0.0006m, row.EstimatedCostUsd);   // gpt-4o-mini, not gpt-4o
    }
}
