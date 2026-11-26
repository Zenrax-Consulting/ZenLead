using ZenLead.Application.Dtos.Campaigns;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Enums;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Application.Campaigns;

public class CampaignStepsUseCaseTests : IDisposable
{
    private readonly CampaignHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static StepRequest Req(int delay = 2, string subject = "Follow up", string body = "Body") => new(delay, subject, body, false);

    [Fact]
    public async Task Add_Appends_With_Contiguous_Order()
    {
        var campaign = await _h.AddCampaignAsync();

        var first = await _h.Run(s => s.Steps.AddAsync(_h.WorkspaceA, campaign.Id, Req(0)));
        var second = await _h.Run(s => s.Steps.AddAsync(_h.WorkspaceA, campaign.Id, Req(3)));

        Assert.Equal(1, first.Value!.Order);
        Assert.Equal(2, second.Value!.Order);
        Assert.Equal([1, 2], (await _h.StepsOf(campaign.Id)).Select(s => s.Order));
    }

    [Fact]
    public async Task Delete_And_Reorder_Are_Free_In_Draft()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Draft, steps: [Step(0, "One"), Step(2, "Two"), Step(4, "Three")]);
        var steps = await _h.StepsOf(campaign.Id);

        var reorder = await _h.Run(s => s.Steps.ReorderAsync(_h.WorkspaceA, campaign.Id, new ReorderStepsRequest([steps[2].Id, steps[0].Id, steps[1].Id])));
        Assert.Equal(CampaignOutcomeKind.Ok, reorder.Kind);
        Assert.Equal(["Three", "One", "Two"], (await _h.StepsOf(campaign.Id)).Select(s => s.SubjectTemplate));

        var delete = await _h.Run(s => s.Steps.DeleteAsync(_h.WorkspaceA, campaign.Id, steps[0].Id));
        Assert.Equal(CampaignOutcomeKind.Ok, delete.Kind);
        Assert.Equal(["Three", "Two"], (await _h.StepsOf(campaign.Id)).Select(s => s.SubjectTemplate));
        Assert.Equal([1, 2], (await _h.StepsOf(campaign.Id)).Select(s => s.Order));
    }

    [Fact]
    public async Task Delete_Refused_Once_An_Enrollment_Is_Past_The_Step_But_Allowed_For_A_Later_Untouched_Step()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, steps: [Step(0, "One"), Step(2, "Two"), Step(4, "Three")]);
        var steps = await _h.StepsOf(campaign.Id);
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Jane Doe"), nextStepOrder: 2);   // step 1 sent

        var refused = await _h.Run(s => s.Steps.DeleteAsync(_h.WorkspaceA, campaign.Id, steps[0].Id));
        Assert.Equal(CampaignOutcomeKind.Conflict, refused.Kind);
        Assert.Equal(CampaignCodes.StepInUse, refused.Code);

        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Steps.DeleteAsync(_h.WorkspaceA, campaign.Id, steps[2].Id))).Kind);
        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Steps.DeleteAsync(_h.WorkspaceA, campaign.Id, steps[1].Id))).Kind); // scheduled into, not past
        Assert.Equal(["One"], (await _h.StepsOf(campaign.Id)).Select(s => s.SubjectTemplate));
    }

    [Fact]
    public async Task Reorder_Refused_Once_Anyone_Got_The_First_Step()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Paused, steps: [Step(0, "One"), Step(2, "Two")]);
        var steps = await _h.StepsOf(campaign.Id);
        var request = new ReorderStepsRequest([steps[1].Id, steps[0].Id]);

        var lead = await _h.AddLeadAsync("Jane Doe");
        await _h.AddEnrollmentAsync(campaign, lead, nextStepOrder: 1);                              // scheduled for step 1, nothing sent
        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Steps.ReorderAsync(_h.WorkspaceA, campaign.Id, request))).Kind);

        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("John Roe"), nextStepOrder: 2);
        var refused = await _h.Run(s => s.Steps.ReorderAsync(_h.WorkspaceA, campaign.Id, new ReorderStepsRequest([steps[0].Id, steps[1].Id])));
        Assert.Equal(CampaignCodes.StepInUse, refused.Code);
    }

    [Fact]
    public async Task Reorder_Must_List_Every_Step_Once()
    {
        var campaign = await _h.AddCampaignAsync(steps: [Step(0, "One"), Step(2, "Two")]);
        var steps = await _h.StepsOf(campaign.Id);

        var missing = await _h.Run(s => s.Steps.ReorderAsync(_h.WorkspaceA, campaign.Id, new ReorderStepsRequest([steps[0].Id])));
        var foreign = await _h.Run(s => s.Steps.ReorderAsync(_h.WorkspaceA, campaign.Id, new ReorderStepsRequest([steps[0].Id, Guid.NewGuid()])));

        Assert.Equal(CampaignOutcomeKind.Invalid, missing.Kind);
        Assert.Equal(CampaignOutcomeKind.Invalid, foreign.Kind);
    }

    [Fact]
    public async Task Deleting_Step_One_Renumbers_And_Rezeroes_The_New_First_Delay()
    {
        var campaign = await _h.AddCampaignAsync(steps: [Step(0, "One"), Step(3, "Two"), Step(5, "Three")]);
        var steps = await _h.StepsOf(campaign.Id);

        await _h.Run(s => s.Steps.DeleteAsync(_h.WorkspaceA, campaign.Id, steps[0].Id));

        var after = await _h.StepsOf(campaign.Id);
        Assert.Equal([(1, "Two", 0), (2, "Three", 5)], after.Select(s => (s.Order, s.SubjectTemplate, s.DelayDays)));
    }

    [Fact]
    public async Task Updating_A_Past_Steps_Text_Is_Allowed()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, steps: [Step(0, "One"), Step(2, "Two")]);
        var steps = await _h.StepsOf(campaign.Id);
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Jane Doe"), nextStepOrder: 3, status: EnrollmentStatus.Completed);

        var outcome = await _h.Run(s => s.Steps.UpdateAsync(_h.WorkspaceA, campaign.Id, steps[0].Id, Req(0, "One (edited)", "New body")));

        Assert.Equal(CampaignOutcomeKind.Ok, outcome.Kind);
        Assert.Equal("One (edited)", (await _h.StepsOf(campaign.Id))[0].SubjectTemplate);
    }

    [Fact]
    public async Task Changing_DelayDays_Keeps_An_Already_Stamped_NextSendAt()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, steps: [Step(0, "One"), Step(2, "Two")]);
        var steps = await _h.StepsOf(campaign.Id);
        var scheduled = new DateTime(2026, 3, 13, 6, 0, 0);
        var e = await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Jane Doe"), nextStepOrder: 2, nextSendAt: scheduled);

        await _h.Run(s => s.Steps.UpdateAsync(_h.WorkspaceA, campaign.Id, steps[1].Id, Req(9, "Two", "Body")));

        Assert.Equal(9, (await _h.StepsOf(campaign.Id))[1].DelayDays);
        Assert.Equal(scheduled, (await _h.EnrollmentsOf(campaign.Id)).Single(x => x.Id == e.Id).NextSendAt);
    }

    [Fact]
    public async Task Other_Workspace_Gets_NotFound()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var step = (await _h.StepsOf(campaign.Id))[0];

        Assert.Equal(CampaignOutcomeKind.NotFound, (await _h.Run(s => s.Steps.AddAsync(_h.WorkspaceB, campaign.Id, Req()), _h.WorkspaceB)).Kind);
        Assert.Equal(CampaignOutcomeKind.NotFound, (await _h.Run(s => s.Steps.UpdateAsync(_h.WorkspaceB, campaign.Id, step.Id, Req()), _h.WorkspaceB)).Kind);
        Assert.Equal(CampaignOutcomeKind.NotFound, (await _h.Run(s => s.Steps.DeleteAsync(_h.WorkspaceB, campaign.Id, step.Id), _h.WorkspaceB)).Kind);
        Assert.Single(await _h.StepsOf(campaign.Id));
    }
}
