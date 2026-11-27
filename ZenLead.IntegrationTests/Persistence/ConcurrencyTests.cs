using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZenLead.Application.Abstractions;
using ZenLead.Application.UseCases.Sending;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;
using ZenLead.Tests.Support.Builders;

namespace ZenLead.IntegrationTests.Persistence;

/// <summary>
/// Races that only real SQL Server transactions and unique indexes can settle (F22, F23): two requests enrolling the same lead, and
/// two sender runs claiming the same due enrollment. The second must lose cleanly (mapped duplicate / skipped), never send twice.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public class ConcurrencyTests(IntegrationApiFactory factory) : IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TwoContexts_EnrollingTheSameLead_OneWins_TheOtherGetsTheMappedDuplicate()
    {
        Campaign campaign;
        Lead lead;
        await using (var db = factory.Sql.CreateContext())
        {
            var ws = await new WorkspaceBuilder().SaveAsync(db);
            campaign = await new CampaignBuilder(ws.Id).SaveAsync(db);
            lead = await new LeadBuilder(ws.Id).SaveAsync(db);
        }

        await using var first = factory.Sql.CreateContext(campaign.WorkspaceId);
        await using var second = factory.Sql.CreateContext(campaign.WorkspaceId);
        var repoA = new CampaignEnrollmentRepository(first);
        var repoB = new CampaignEnrollmentRepository(second);

        // both requests check "already enrolled?" before either saves: the read can't protect them, only the unique index can
        Assert.Empty(await repoA.ExistingLeadIdsAsync(campaign.Id, [lead.Id]));
        Assert.Empty(await repoB.ExistingLeadIdsAsync(campaign.Id, [lead.Id]));
        await repoA.AddRangeAsync([new EnrollmentBuilder(campaign, lead).Build()]);

        await Assert.ThrowsAsync<DuplicateEnrollmentException>(() => repoB.AddRangeAsync([new EnrollmentBuilder(campaign, lead).Build()]));
        await using var check = factory.Sql.CreateContext();
        Assert.Equal(1, await check.CampaignEnrollments.IgnoreQueryFilters().CountAsync(e => e.CampaignId == campaign.Id));
    }

    [Fact]
    public async Task TwoParallelEnrollRequests_ForTheSameLeads_EnrollEachLeadOnce_AndNeither500s()
    {
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync();
        Campaign campaign;
        List<Guid> leadIds = [];
        await using (var db = factory.Sql.CreateContext())
        {
            campaign = await new CampaignBuilder(workspaceId).WithStep(0, "Hello", "Hi there").SaveAsync(db);
            for (var i = 0; i < 20; i++) leadIds.Add((await new LeadBuilder(workspaceId).SaveAsync(db)).Id);
        }
        var client = factory.ClientFor(token);

        var responses = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => client.PostJsonAsync($"/api/v1/campaigns/{campaign.Id}/enroll", new { leadIds })));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode || (int)r.StatusCode == 409, $"Got {(int)r.StatusCode}: {r.Content.ReadAsStringAsync().Result}"));
        await using var check = factory.Sql.CreateContext();
        Assert.Equal(20, await check.CampaignEnrollments.IgnoreQueryFilters().CountAsync(e => e.CampaignId == campaign.Id));
    }

    [Fact]
    public async Task TwoSenderRuns_ClaimingTheSameDueEnrollment_SendExactlyOneEmail()
    {
        DueSend due;
        await using (var db = factory.Sql.CreateContext())
            due = await SendingSeed.DueEnrollmentAsync(db, factory.Clock.GetUtcNow().UtcDateTime);

        // two scopes = two runners (the recurring job's DisableConcurrentExecution is the first guard; this is the backstop)
        var summaries = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => factory.InScopeAsync(sp =>
            sp.GetRequiredService<SendDueEmailsUseCase>().ExecuteAsync(CancellationToken.None))));

        Assert.Equal(1, summaries.Sum(s => s.Sent));
        Assert.Single(factory.Stubs.SendGrid.MailSends);
        await using var check = factory.Sql.CreateContext();
        var message = Assert.Single(await check.EmailMessages.IgnoreQueryFilters().Where(m => m.EnrollmentId == due.Enrollment.Id).ToListAsync());
        Assert.Equal(EmailMessageStatus.Sent, message.Status);
        Assert.Equal(SendGridStub.MessageId, message.ProviderMessageId);
        var enrollment = await check.CampaignEnrollments.IgnoreQueryFilters().SingleAsync(e => e.Id == due.Enrollment.Id);
        Assert.Equal(2, enrollment.NextStepOrder);
    }
}
