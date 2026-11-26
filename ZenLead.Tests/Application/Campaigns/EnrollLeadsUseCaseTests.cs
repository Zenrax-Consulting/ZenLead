using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Enums;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Application.Campaigns;

public class EnrollLeadsUseCaseTests : IDisposable
{
    private readonly CampaignHarness _h = new();
    public void Dispose() => _h.Dispose();

    private Task<CampaignOutcome<EnrollResult>> Enroll(Guid campaignId, IReadOnlyList<Guid>? ids, bool dryRun = false, LeadQuery? filter = null, Guid? workspace = null)
        => _h.Run(s => s.Enroll.ExecuteAsync(workspace ?? _h.WorkspaceA, campaignId, new EnrollRequest(ids, filter, dryRun)), workspace);

    [Fact]
    public async Task Skips_Suppressed_And_Invalid_Leads()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var ok = await _h.AddLeadAsync("Jane Doe");
        var unsub = await _h.AddLeadAsync("Un Sub", status: LeadStatus.Unsubscribed);
        var bounced = await _h.AddLeadAsync("Boun Ced", status: LeadStatus.Bounced);
        var invalid = await _h.AddLeadAsync("In Valid", verification: EmailVerificationStatus.Invalid);

        var result = (await Enroll(campaign.Id, [ok.Id, unsub.Id, bounced.Id, invalid.Id])).Value!;

        Assert.Equal(1, result.Enrolled);
        Assert.Equal(2, result.SkippedSuppressed);
        Assert.Equal(1, result.SkippedInvalidEmail);
        Assert.Equal([ok.Id], (await _h.EnrollmentsOf(campaign.Id)).Select(e => e.LeadId));
    }

    [Fact]
    public async Task Already_Enrolled_Is_Counted_Not_Duplicated()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var a = await _h.AddLeadAsync("Jane Doe");
        var b = await _h.AddLeadAsync("John Roe");

        Assert.Equal(2, (await Enroll(campaign.Id, [a.Id, b.Id])).Value!.Enrolled);
        var again = (await Enroll(campaign.Id, [a.Id, b.Id, a.Id])).Value!;

        Assert.Equal(0, again.Enrolled);
        Assert.Equal(2, again.AlreadyEnrolled);
        Assert.Equal(2, (await _h.EnrollmentsOf(campaign.Id)).Count);
    }

    [Fact]
    public async Task Missing_Required_Token_Is_Skipped_But_A_Fallbacked_One_Enrolls()
    {
        var needsCompany = await _h.AddCampaignAsync(steps: Step(body: "We help {{company}}"));
        var fallback = await _h.AddCampaignAsync(steps: Step(body: "We help {{company|teams like yours}}"));
        var noCompany = await _h.AddLeadAsync("Jane Doe", company: null);

        Assert.Equal(1, (await Enroll(needsCompany.Id, [noCompany.Id])).Value!.SkippedMissingTokens);
        Assert.Equal(1, (await Enroll(fallback.Id, [noCompany.Id])).Value!.Enrolled);
    }

    [Fact]
    public async Task Unverified_And_Risky_Are_Enrolled_And_Counted_As_Warnings()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var unverified = await _h.AddLeadAsync("Un Verified", verification: EmailVerificationStatus.Unverified);
        var risky = await _h.AddLeadAsync("Ris Ky", verification: EmailVerificationStatus.Risky);

        var result = (await Enroll(campaign.Id, [unverified.Id, risky.Id])).Value!;

        Assert.Equal(2, result.Enrolled);
        Assert.Equal(1, result.WarnUnverified);
        Assert.Equal(1, result.WarnRisky);
    }

    [Fact]
    public async Task DryRun_Returns_Identical_Counts_And_Writes_Nothing()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step(body: "{{company}}"));
        var ids = new List<Guid>
        {
            (await _h.AddLeadAsync("Jane Doe")).Id,
            (await _h.AddLeadAsync("Un Sub", status: LeadStatus.Unsubscribed)).Id,
            (await _h.AddLeadAsync("No Co", company: null)).Id,
            (await _h.AddLeadAsync("Ris Ky", verification: EmailVerificationStatus.Risky)).Id,
            Guid.NewGuid()
        };

        var dry = (await Enroll(campaign.Id, ids, dryRun: true)).Value!;
        Assert.Empty(await _h.EnrollmentsOf(campaign.Id));

        var real = (await Enroll(campaign.Id, ids)).Value!;
        Assert.Equal(dry, real);
        Assert.Equal(2, (await _h.EnrollmentsOf(campaign.Id)).Count);
    }

    [Fact]
    public async Task Draft_Leaves_NextSendAt_Null_Active_Stamps_It_Inside_The_Window()
    {
        var draft = await _h.AddCampaignAsync(CampaignStatus.Draft, steps: Step());
        var active = await _h.AddCampaignAsync(CampaignStatus.Active, steps: Step());
        var lead = await _h.AddLeadAsync("Jane Doe");
        _h.Clock.Now = new DateTimeOffset(2026, 3, 13, 14, 0, 0, TimeSpan.Zero);    // Fri 18:00 Dubai — after the 17:00 end

        await Enroll(draft.Id, [lead.Id]);
        await Enroll(active.Id, [lead.Id]);

        Assert.Null((await _h.EnrollmentsOf(draft.Id)).Single().NextSendAt);
        var stamped = (await _h.EnrollmentsOf(active.Id)).Single();
        Assert.Equal(new DateTime(2026, 3, 16, 5, 0, 0), stamped.NextSendAt);       // Mon 09:00 Dubai
        Assert.Equal(1, stamped.NextStepOrder);
        Assert.Equal(EnrollmentStatus.Active, stamped.Status);
    }

    [Fact]
    public async Task Completed_Campaign_Is_Refused()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Completed, steps: Step());
        var outcome = await Enroll(campaign.Id, [(await _h.AddLeadAsync("Jane Doe")).Id]);

        Assert.Equal(CampaignOutcomeKind.Conflict, outcome.Kind);
        Assert.Equal(CampaignCodes.CampaignCompleted, outcome.Code);
    }

    [Fact]
    public async Task Filter_Respects_The_Cap_And_Reports_Truncation()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        for (var i = 0; i < 3; i++) await _h.AddLeadAsync($"Lead {i}");

        var all = (await Enroll(campaign.Id, null, filter: new LeadQuery())).Value!;
        Assert.Equal(3, all.Enrolled);
        Assert.False(all.Truncated);

        // the 5,000 cap itself: ListIdsAsync is asked for one more than the cap; a list longer than the cap is cut and flagged
        var tooMany = Enumerable.Range(0, EnrollLeadsUseCase.MaxPerRequest + 1).Select(_ => Guid.NewGuid()).ToList();
        var capped = (await Enroll(campaign.Id, tooMany, dryRun: true)).Value!;
        Assert.True(capped.Truncated);
        Assert.Equal(EnrollLeadsUseCase.MaxPerRequest, capped.NotFound);
    }

    [Fact]
    public async Task Other_Workspaces_Leads_Count_As_NotFound()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var theirs = await _h.AddLeadAsync("Their Lead", workspace: _h.WorkspaceB);
        var mine = await _h.AddLeadAsync("My Lead");

        var result = (await Enroll(campaign.Id, [theirs.Id, mine.Id])).Value!;

        Assert.Equal(1, result.Enrolled);
        Assert.Equal(1, result.NotFound);
        Assert.Equal([mine.Id], (await _h.EnrollmentsOf(campaign.Id)).Select(e => e.LeadId));
    }

    [Fact]
    public async Task Other_Workspaces_Campaign_Is_NotFound()
    {
        var theirs = await _h.AddCampaignAsync(workspace: _h.WorkspaceB, steps: Step());
        var lead = await _h.AddLeadAsync("Jane Doe");

        Assert.Equal(CampaignOutcomeKind.NotFound, (await Enroll(theirs.Id, [lead.Id])).Kind);
        Assert.Empty(await _h.EnrollmentsOf(theirs.Id));
    }

    [Fact]
    public async Task Address_on_the_suppression_list_is_skipped_as_suppressed()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var ok = await _h.AddLeadAsync("Jane Doe");
        var listed = await _h.AddLeadAsync("Listed Lead");
        await _h.Run(async s =>
        {
            await s.Suppression.AddAsync(_h.WorkspaceA, listed.Email, SuppressionReason.SpamReport, null, CancellationToken.None);
            return 0;
        });

        var result = (await Enroll(campaign.Id, [ok.Id, listed.Id])).Value!;

        Assert.Equal(1, result.Enrolled);
        Assert.Equal(1, result.SkippedSuppressed);
        Assert.Equal([ok.Id], (await _h.EnrollmentsOf(campaign.Id)).Select(e => e.LeadId));
    }
}
