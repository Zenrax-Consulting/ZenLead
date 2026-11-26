using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Email;
using ZenLead.Application.Sending;
using ZenLead.Application.UseCases.Campaigns;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Support;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Application.Campaigns;

/// <summary>The small backend additions the campaigns UI needs (F24): StepsStarted, Sent, activation preview, problem step orders.</summary>
public class CampaignUiAdditionsTests : IDisposable
{
    private readonly CampaignHarness _h = new();
    public void Dispose() => _h.Dispose();

    // 600 in × $0.01/1K + 300 out × $0.02/1K = $0.012 per assumed call: easy to check by hand.
    private static readonly AiPricing Pricing = new() { PricePer1KInputUsd = 0.01m, PricePer1KOutputUsd = 0.02m };

    private ActivationPreviewUseCase Preview(Scope s, FakeAiBudget? budget = null)
        => new(s.Campaigns, s.Enrollments, Pricing, budget ?? new FakeAiBudget());

    private Task MarkAiAsync(Guid campaignId, params int[] orders)
        => _h.Read(async ctx =>
        {
            var steps = await ctx.CampaignSteps.IgnoreQueryFilters().Where(s => s.CampaignId == campaignId && orders.Contains(s.Order)).ToListAsync();
            steps.ForEach(s => s.UseAiPersonalisation = true);
            return await ctx.SaveChangesAsync();
        });

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    public void StepsStarted_Is_Max_Next_Step_Minus_One_Never_Negative(int max, int expected)
        => Assert.Equal(expected, CampaignsUseCase.StepsStarted(max));

    [Fact]
    public async Task Get_Reports_StepsStarted_And_Both_Zones()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, null, Step(), Step(3), Step(4));
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Jane Doe"), nextStepOrder: 3);
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("John Roe"), nextStepOrder: 1);

        var response = (await _h.Run(s => s.Manage.GetAsync(_h.WorkspaceA, campaign.Id))).Value!;

        Assert.Equal(2, response.StepsStarted);
        Assert.Equal("Asia/Dubai", response.WorkspaceTimeZone);
        Assert.Equal("Asia/Dubai", response.EffectiveTimeZone);
    }

    [Fact]
    public async Task New_Campaign_Has_No_Started_Steps()
    {
        var created = await _h.Run(s => s.Manage.CreateAsync(_h.WorkspaceA, _h.UserId,
            new("X", null, 30, "09:00", "17:00", SendDays.Weekdays, "Europe/London")));

        Assert.Equal(0, created.StepsStarted);
        Assert.Equal("Asia/Dubai", created.WorkspaceTimeZone);
        Assert.Equal("Europe/London", created.EffectiveTimeZone);
    }

    [Fact]
    public async Task List_Counts_Sent_As_Sent_Delivered_Or_Bounced_Per_Campaign()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, null, Step());
        var other = await _h.AddCampaignAsync(CampaignStatus.Active, null, Step());
        var statuses = new[] { EmailMessageStatus.Queued, EmailMessageStatus.Sent, EmailMessageStatus.Delivered,
                               EmailMessageStatus.Bounced, EmailMessageStatus.Dropped, EmailMessageStatus.Failed };
        var messages = new List<EmailMessage>();
        foreach (var status in statuses)
        {
            var lead = await _h.AddLeadAsync($"Lead {status}");
            var e = await _h.AddEnrollmentAsync(campaign, lead);
            messages.Add(new EmailMessage
            {
                Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, CampaignId = campaign.Id, EnrollmentId = e.Id, StepId = campaign.Steps[0].Id,
                LeadId = lead.Id, Subject = "s", Body = "b", Status = status, QueuedAt = DateTime.UtcNow
            });
        }
        await _h.Read(ctx => { ctx.EmailMessages.AddRange(messages); return ctx.SaveChangesAsync(); });

        var list = await _h.Run(s => s.Manage.ListAsync(_h.WorkspaceA));

        Assert.Equal(3, list.Single(c => c.Id == campaign.Id).Sent);
        Assert.Equal(0, list.Single(c => c.Id == other.Id).Sent);
        Assert.Equal(6, list.Single(c => c.Id == campaign.Id).Counts.Total);
    }

    [Fact]
    public async Task Activation_Preview_Estimates_Live_Enrollments_Times_Ai_Steps()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Draft, null, Step(), Step(3), Step(4));
        await MarkAiAsync(campaign.Id, 1, 3);
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("A"));
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("B"));
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("C"), status: EnrollmentStatus.Paused);
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("D"), status: EnrollmentStatus.Completed);   // not live: no calls

        var preview = (await _h.Run(s => Preview(s).ExecuteAsync(_h.WorkspaceA, campaign.Id))).Value!;

        Assert.Equal(3, preview.EnrolledActive);
        Assert.Equal(2, preview.AiSteps);
        Assert.Equal(6, preview.EstimatedAiCalls);
        Assert.Equal(6 * 0.012m, preview.EstimatedAiCostUsd);
        Assert.Equal(19m, preview.AiBudgetRemainingUsd);           // FakeAiBudget: $1 of $20 spent
    }

    [Fact]
    public async Task Activation_Preview_Without_Ai_Steps_Is_Zero_And_Remaining_Never_Negative()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Draft, null, Step());
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("A"));

        var preview = (await _h.Run(s => Preview(s, new FakeAiBudget { State = BudgetState.Exceeded })
            .ExecuteAsync(_h.WorkspaceA, campaign.Id))).Value!;

        Assert.Equal(0, preview.EstimatedAiCalls);
        Assert.Equal(0m, preview.EstimatedAiCostUsd);
        Assert.Equal(0m, preview.AiBudgetRemainingUsd);
    }

    [Fact]
    public async Task Activation_Preview_Of_Another_Workspaces_Campaign_Is_Not_Found()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Draft, _h.WorkspaceB, Step());

        var outcome = await _h.Run(s => Preview(s).ExecuteAsync(_h.WorkspaceA, campaign.Id));

        Assert.Equal(CampaignOutcomeKind.NotFound, outcome.Kind);
    }

    [Fact]
    public void Step_Level_Problems_Carry_The_Step_Order()
    {
        var c = new Campaign
        {
            Steps =
            [
                new CampaignStep { Order = 1, DelayDays = 2, SubjectTemplate = "Hi", BodyTemplate = "Body" },
                new CampaignStep { Order = 2, DelayDays = 3, SubjectTemplate = "", BodyTemplate = "{{colour}}" }
            ]
        };

        var problems = ActivateCampaignUseCase.Validate(c, new EmailOptions { DomainVerified = false });

        Assert.Equal(1, problems.Single(p => p.Code == "first_delay").StepOrder);
        Assert.Equal(2, problems.Single(p => p.Code == "empty_subject").StepOrder);
        Assert.Equal(2, problems.Single(p => p.Code == "bad_token").StepOrder);
        Assert.Null(problems.Single(p => p.Code == "domain_unverified").StepOrder);
    }
}
