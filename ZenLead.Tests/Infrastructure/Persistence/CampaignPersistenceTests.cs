using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Application.Campaigns;
using static ZenLead.Tests.Application.Campaigns.CampaignHarness;

namespace ZenLead.Tests.Infrastructure.Persistence;

public class CampaignPersistenceTests : IDisposable
{
    private readonly CampaignHarness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Lead_Can_Be_Enrolled_Only_Once_Per_Campaign()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var lead = await _h.AddLeadAsync("Jane Doe");
        await _h.AddEnrollmentAsync(campaign, lead);

        await Assert.ThrowsAsync<DbUpdateException>(() => _h.AddEnrollmentAsync(campaign, lead));
    }

    [Fact]
    public async Task Repository_Turns_A_Duplicate_Enrollment_Into_DuplicateEnrollmentException()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var lead = await _h.AddLeadAsync("Jane Doe");
        await _h.AddEnrollmentAsync(campaign, lead);

        await using var ctx = _h.Db.CreateContext(_h.WorkspaceA);
        await Assert.ThrowsAsync<ZenLead.Application.Abstractions.DuplicateEnrollmentException>(() => new CampaignEnrollmentRepository(ctx).AddRangeAsync(
            [new CampaignEnrollment { Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, CampaignId = campaign.Id, LeadId = lead.Id, EnrolledAt = DateTime.UtcNow }]));
    }

    [Fact]
    public async Task Step_Order_Is_Unique_Per_Campaign()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        await using var ctx = _h.Db.CreateContext(null);
        ctx.CampaignSteps.Add(new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = _h.WorkspaceA, CampaignId = campaign.Id, Order = 1, SubjectTemplate = "s", BodyTemplate = "b" });

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Campaigns_Steps_And_Enrollments_Are_Tenant_Isolated()
    {
        var a = await _h.AddCampaignAsync(steps: Step());
        var b = await _h.AddCampaignAsync(workspace: _h.WorkspaceB, steps: Step());
        await _h.AddEnrollmentAsync(a, await _h.AddLeadAsync("Jane Doe"));
        await _h.AddEnrollmentAsync(b, await _h.AddLeadAsync("John Roe", workspace: _h.WorkspaceB));

        await using var ctx = _h.Db.CreateContext(_h.WorkspaceA);
        Assert.Equal([a.Id], await ctx.Campaigns.Select(c => c.Id).ToListAsync());
        Assert.All(await ctx.CampaignSteps.ToListAsync(), s => Assert.Equal(_h.WorkspaceA, s.WorkspaceId));
        Assert.All(await ctx.CampaignEnrollments.ToListAsync(), e => Assert.Equal(_h.WorkspaceA, e.WorkspaceId));
        Assert.Null(await new CampaignRepository(ctx).GetWithStepsAsync(b.Id));

        await using var none = _h.Db.CreateContext(null);
        Assert.Empty(await none.Campaigns.ToListAsync());
        Assert.Empty(await none.CampaignSteps.ToListAsync());
        Assert.Empty(await none.CampaignEnrollments.ToListAsync());
    }

    [Fact]
    public void Campaign_Entities_Are_Tenant_Entities()
    {
        using var ctx = _h.Db.CreateContext(null);
        foreach (var type in new[] { typeof(Campaign), typeof(CampaignStep), typeof(CampaignEnrollment) })
        {
            Assert.True(typeof(ITenantEntity).IsAssignableFrom(type));
            Assert.NotEmpty(ctx.Model.FindEntityType(type)!.GetDeclaredQueryFilters());
        }
    }

    [Fact]
    public async Task ListWithCounts_Returns_Grouped_Counts_In_One_Query()
    {
        var older = await _h.AddCampaignAsync(steps: [Step(), Step(2)]);
        await Task.Delay(5);
        var newer = await _h.AddCampaignAsync(steps: Step());
        var statuses = new[] { EnrollmentStatus.Active, EnrollmentStatus.Paused, EnrollmentStatus.Completed, EnrollmentStatus.Replied,
                               EnrollmentStatus.Unsubscribed, EnrollmentStatus.Bounced, EnrollmentStatus.Failed, EnrollmentStatus.Active };
        foreach (var s in statuses) await _h.AddEnrollmentAsync(older, await _h.AddLeadAsync("Lead " + s), status: s);
        await _h.AddCampaignAsync(workspace: _h.WorkspaceB, steps: Step());

        var counter = new CommandCounter();
        await using var ctx = _h.Db.CreateContext(_h.WorkspaceA, counter);
        var list = await new CampaignRepository(ctx).ListWithCountsAsync(_h.WorkspaceA);

        Assert.Equal(1, counter.Count);
        Assert.Equal([newer.Id, older.Id], list.Select(c => c.Id));
        var o = list[1];
        Assert.Equal(2, o.StepCount);
        Assert.Equal(new(8, 3, 1, 1, 1, 1, 1), o.Counts);
        Assert.Equal(new(0, 0, 0, 0, 0, 0, 0), list[0].Counts);
    }

    [Fact]
    public async Task Sender_Due_Query_Runs_And_Has_Its_Index()
    {
        var campaign = await _h.AddCampaignAsync(CampaignStatus.Active, steps: Step());
        var now = new DateTime(2026, 3, 11, 6, 0, 0);
        var due = await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Due Lead"), nextSendAt: now.AddMinutes(-1));
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Later Lead"), nextSendAt: now.AddHours(1));
        await _h.AddEnrollmentAsync(campaign, await _h.AddLeadAsync("Done Lead"), nextSendAt: now.AddMinutes(-1), status: EnrollmentStatus.Replied);

        await using var ctx = _h.Db.CreateContext(null);
        var ids = await ctx.CampaignEnrollments.IgnoreQueryFilters()
            .Where(e => e.Status == EnrollmentStatus.Active && e.NextSendAt <= now).Select(e => e.Id).ToListAsync();

        Assert.Equal([due.Id], ids);
        Assert.Contains(ctx.Model.FindEntityType(typeof(CampaignEnrollment))!.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(CampaignEnrollment.Status), nameof(CampaignEnrollment.NextSendAt)]));
    }

    [Fact]
    public async Task Enrollment_List_Keeps_Soft_Deleted_Leads()
    {
        var campaign = await _h.AddCampaignAsync(steps: Step());
        var lead = await _h.AddLeadAsync("Gone Lead");
        await _h.AddEnrollmentAsync(campaign, lead);
        await using (var del = _h.Db.CreateContext(null))
        {
            var l = await del.Leads.IgnoreQueryFilters().SingleAsync(x => x.Id == lead.Id);
            l.DeletedAt = DateTime.UtcNow;
            await del.SaveChangesAsync();
        }

        await using var ctx = _h.Db.CreateContext(_h.WorkspaceA);
        var page = await new CampaignEnrollmentRepository(ctx).ListAsync(campaign.Id, null, 1, 25);

        Assert.Equal(1, page.Total);
        Assert.Equal("Gone Lead", Assert.Single(page.Items).Lead!.Name);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
