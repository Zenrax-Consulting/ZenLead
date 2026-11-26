using ZenLead.Application.Email;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Application.Campaigns;

public class ActivateCampaignUseCaseTests : IDisposable
{
    private readonly CampaignHarness _h = new();
    public void Dispose() => _h.Dispose();

    private static readonly EmailOptions Verified = new() { DomainVerified = true };

    private static Campaign Valid() => new()
    {
        Steps =
        [
            new CampaignStep { Order = 1, DelayDays = 0, SubjectTemplate = "Hi {{firstName}}", BodyTemplate = "Body" },
            new CampaignStep { Order = 2, DelayDays = 3, SubjectTemplate = "Re: hi", BodyTemplate = "Body 2" }
        ]
    };

    private static string[] Codes(Campaign c, EmailOptions? email = null)
        => ActivateCampaignUseCase.Validate(c, email ?? Verified).Select(p => p.Code).ToArray();

    [Fact]
    public void Valid_Campaign_Has_No_Problems() => Assert.Empty(Codes(Valid()));

    [Fact]
    public void No_Steps() => Assert.Equal(["no_steps"], Codes(new Campaign()));

    [Fact]
    public void Steps_Unordered()
    {
        var c = Valid();
        c.Steps[1].Order = 3;
        Assert.Equal(["steps_unordered"], Codes(c));
    }

    [Fact]
    public void First_Delay()
    {
        var c = Valid();
        c.Steps[0].DelayDays = 2;
        Assert.Equal(["first_delay"], Codes(c));
    }

    [Fact]
    public void Empty_Subject_And_Body()
    {
        var c = Valid();
        c.Steps[0].SubjectTemplate = " ";
        Assert.Equal(["empty_subject"], Codes(c));
        c.Steps[0].SubjectTemplate = "ok";
        c.Steps[1].BodyTemplate = "";
        Assert.Equal(["empty_body"], Codes(c));
    }

    [Fact]
    public void Bad_Token()
    {
        var c = Valid();
        c.Steps[1].BodyTemplate = "Loved {{favouriteBand}}";
        var problem = Assert.Single(ActivateCampaignUseCase.Validate(c, Verified));
        Assert.Equal("bad_token", problem.Code);
        Assert.Contains("{{favouriteBand}}", problem.Message);
    }

    [Fact]
    public void Window_Invalid()
    {
        var c = Valid();
        c.WindowStart = new TimeOnly(18, 0);
        Assert.Equal(["window_invalid"], Codes(c));
        c.WindowStart = new TimeOnly(9, 0);
        c.SendDays = SendDays.None;
        Assert.Equal(["window_invalid"], Codes(c));
    }

    [Fact]
    public void Unverified_Domain_Blocks() => Assert.Equal(["domain_unverified"], Codes(Valid(), new EmailOptions { DomainVerified = false }));

    [Fact]
    public async Task Activation_Problems_Are_A_Conflict_And_Change_Nothing()
    {
        _h.Email.DomainVerified = false;
        var campaign = await _h.AddCampaignAsync(steps: Step());

        var outcome = await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceA, campaign.Id));

        Assert.Equal(CampaignOutcomeKind.Conflict, outcome.Kind);
        Assert.Contains(outcome.Problems!, p => p.Code == "domain_unverified");
        Assert.Equal(CampaignStatus.Draft, (await _h.CampaignById(campaign.Id)).Status);
    }

    [Fact]
    public async Task Success_Stamps_Only_Enrollments_Without_NextSendAt()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var fresh = await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Jane Doe"));
        var scheduled = new DateTime(2026, 3, 20, 7, 0, 0);
        var kept = await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("John Roe"), nextSendAt: scheduled);
        var finished = await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Done Lead"), status: EnrollmentStatus.Replied);

        var outcome = await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceA, campaign.Id));

        Assert.Equal(CampaignOutcomeKind.Ok, outcome.Kind);
        var c = await _h.CampaignById(campaign.Id);
        Assert.Equal(CampaignStatus.Active, c.Status);
        Assert.Equal(_h.Clock.Now.UtcDateTime, c.ActivatedAt);
        var rows = (await _h.EnrollmentsOf(campaign.Id)).ToDictionary(e => e.Id);
        Assert.Equal(new DateTime(2026, 3, 11, 6, 0, 0), rows[fresh.Id].NextSendAt);   // Wed 10:00 Dubai: inside the window, now
        Assert.Equal(scheduled, rows[kept.Id].NextSendAt);
        Assert.Null(rows[finished.Id].NextSendAt);
    }

    [Fact]
    public async Task Pause_And_Resume()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceA, campaign.Id))).Kind);
        var firstActivation = (await _h.CampaignById(campaign.Id)).ActivatedAt;

        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Pause.ExecuteAsync(_h.WorkspaceA, campaign.Id))).Kind);
        Assert.Equal(CampaignStatus.Paused, (await _h.CampaignById(campaign.Id)).Status);

        _h.Clock.Now = _h.Clock.Now.AddDays(2);
        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceA, campaign.Id))).Kind);
        var resumed = await _h.CampaignById(campaign.Id);
        Assert.Equal(CampaignStatus.Active, resumed.Status);
        Assert.Equal(firstActivation, resumed.ActivatedAt);                              // ActivatedAt ??= now: the first activation sticks
    }

    [Fact]
    public async Task Draft_Cannot_Be_Paused()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var outcome = await _h.Run(s => s.Pause.ExecuteAsync(_h.WorkspaceA, campaign.Id));

        Assert.Equal(CampaignOutcomeKind.Conflict, outcome.Kind);
        Assert.Equal(CampaignCodes.InvalidTransition, outcome.Code);
    }

    [Fact]
    public async Task Completed_Cannot_Be_Activated()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Completed, steps: Step());
        var outcome = await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceA, campaign.Id));

        Assert.Equal(CampaignOutcomeKind.Conflict, outcome.Kind);
        Assert.Equal(CampaignCodes.CampaignCompleted, outcome.Code);
    }

    [Fact]
    public async Task Activating_An_Active_Campaign_Is_A_NoOp()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, steps: Step());
        Assert.Equal(CampaignOutcomeKind.Ok, (await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceA, campaign.Id))).Kind);
        Assert.Null((await _h.CampaignById(campaign.Id)).ActivatedAt);
    }

    [Fact]
    public async Task Other_Workspace_Gets_NotFound()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        Assert.Equal(CampaignOutcomeKind.NotFound, (await _h.Run(s => s.Activate.ExecuteAsync(_h.WorkspaceB, campaign.Id), _h.WorkspaceB)).Kind);
        Assert.Equal(CampaignOutcomeKind.NotFound, (await _h.Run(s => s.Pause.ExecuteAsync(_h.WorkspaceB, campaign.Id), _h.WorkspaceB)).Kind);
    }
}
