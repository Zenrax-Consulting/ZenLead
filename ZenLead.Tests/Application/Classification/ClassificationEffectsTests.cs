using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Classification;

/// <summary>The manual-override path F27 uses (<c>manual: true</c>).</summary>
public class ClassificationEffectsTests : IDisposable
{
    private readonly ClassificationHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Manual_label_applies_regardless_of_confidence_and_is_recorded_as_Manual()
    {
        var s = await _h.SeedAsync();

        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.Unsubscribe, 0m, manual: true);

        var m = await _h.Message(s.MessageId);
        Assert.Equal(ClassificationSource.Manual, m.ClassifiedBy);
        Assert.Equal(1m, m.ClassificationConfidence);
        Assert.Null(m.ClassificationNote);
        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.LeadId)).Status);
        Assert.Single(await _h.Suppressions());
    }

    [Fact]
    public async Task Manual_label_clears_NeedsReview()
    {
        var s = await _h.SeedAsync();
        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.Unsubscribe, 0.3m, manual: false);
        Assert.True((await _h.Message(s.MessageId)).NeedsReview);

        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.NotInterested, 0m, manual: true);

        var m = await _h.Message(s.MessageId);
        Assert.False(m.NeedsReview);
        Assert.Equal(ReplyClassification.NotInterested, m.Classification);
        Assert.Equal(ReplyClassification.NotInterested, (await _h.Thread(s.ThreadId)).Classification);
    }

    [Fact]
    public async Task Relabelling_an_unsubscribe_as_interested_does_not_silently_unsuppress()
    {
        var s = await _h.SeedAsync();
        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.Unsubscribe, 0.95m, manual: false);

        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.Interested, 0m, manual: true);

        // suppression is sticky: un-suppressing is a separate, deliberate action (out of scope)
        Assert.Single(await _h.Suppressions());
        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.LeadId)).Status);
        Assert.Equal(ReplyClassification.Interested, (await _h.Message(s.MessageId)).Classification);
    }

    [Fact]
    public async Task Manual_out_of_office_resumes_the_sequence()
    {
        var s = await _h.SeedAsync();

        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.OutOfOffice, 0m, manual: true);

        Assert.Equal(EnrollmentStatus.Active, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Contacted, (await _h.Lead(s.LeadId)).Status);
    }

    [Fact]
    public async Task Out_of_office_never_resumes_a_suppressed_lead()
    {
        var s = await _h.SeedAsync(leadStatus: LeadStatus.Unsubscribed);

        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.OutOfOffice, 0.99m, manual: false);

        Assert.Equal(EnrollmentStatus.Replied, (await _h.Enrollment(s.EnrollmentId)).Status);
        Assert.Equal(LeadStatus.Unsubscribed, (await _h.Lead(s.LeadId)).Status);
    }

    [Fact]
    public async Task Unsubscribe_twice_keeps_one_suppression_row()
    {
        var s = await _h.SeedAsync();

        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.Unsubscribe, 0.9m, manual: false);
        await _h.ApplyAsync(s.WorkspaceId, s.MessageId, ReplyClassification.Unsubscribe, 0m, manual: true);

        Assert.Single(await _h.Suppressions());
    }
}
