using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

/// <summary>The super admin has no workspace: its requests run with ICurrentWorkspace = null and must see no tenant data.</summary>
public class NullWorkspaceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _ws = Guid.NewGuid();

    public NullWorkspaceTests()
    {
        using var ctx = _db.CreateContext(null);
        var now = DateTime.UtcNow;
        ctx.Workspaces.Add(new Workspace { Id = _ws, Name = "Acme", AdminEmail = "admin@acme.test", CreatedAt = now });
        var company = new Company { Id = Guid.NewGuid(), WorkspaceId = _ws, Name = "Co", CreatedAt = now };
        ctx.Companies.Add(company);
        var lead = new Lead { Id = Guid.NewGuid(), WorkspaceId = _ws, Name = "n", Email = "a@x.com", Status = LeadStatus.New, CreatedAt = now, CompanyId = company.Id };
        ctx.Leads.Add(lead);
        ctx.AiUsageLogs.Add(new AiUsageLog { Id = Guid.NewGuid(), WorkspaceId = _ws, Model = "m", CreatedAt = now });
        var profile = new TargetProfile { Id = Guid.NewGuid(), WorkspaceId = _ws, Name = "P", CreatedAt = now };
        ctx.TargetProfiles.Add(profile);
        ctx.DiscoveryRuns.Add(new LeadDiscoveryRun { Id = Guid.NewGuid(), WorkspaceId = _ws, TargetProfileId = profile.Id, Provider = "Fake", CreatedAt = now });
        ctx.CsvImportBatches.Add(new CsvImportBatch { Id = Guid.NewGuid(), WorkspaceId = _ws, FileName = "a.csv", BlobPath = "imports/a.csv", CreatedAt = now });
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), WorkspaceId = _ws, Name = "C", CreatedAt = now,
            Steps = [new CampaignStep { Id = Guid.NewGuid(), WorkspaceId = _ws, Order = 1, SubjectTemplate = "s", BodyTemplate = "b" }]
        };
        ctx.Campaigns.Add(campaign);
        var enrollment = new CampaignEnrollment { Id = Guid.NewGuid(), WorkspaceId = _ws, CampaignId = campaign.Id, LeadId = lead.Id, EnrolledAt = now };
        ctx.CampaignEnrollments.Add(enrollment);
        ctx.EmailMessages.Add(new EmailMessage
        {
            Id = Guid.NewGuid(), WorkspaceId = _ws, CampaignId = campaign.Id, EnrollmentId = enrollment.Id, StepId = campaign.Steps[0].Id,
            LeadId = lead.Id, Subject = "s", Body = "b", QueuedAt = now
        });
        ctx.SuppressedEmails.Add(new SuppressedEmail { Id = Guid.NewGuid(), WorkspaceId = _ws, Email = "gone@x.com", CreatedAt = now });
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static readonly MethodInfo CountMethod = typeof(NullWorkspaceTests).GetMethod(nameof(CountAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static async Task<(int Filtered, int Unfiltered)> CountAsync<T>(ZenLeadDbContext ctx) where T : class
        => (await ctx.Set<T>().CountAsync(), await ctx.Set<T>().IgnoreQueryFilters().CountAsync());

    [Fact]
    public async Task Super_admin_context_sees_zero_rows_in_every_tenant_table()
    {
        using var ctx = _db.CreateContext(null);
        var tenantTypes = ctx.Model.GetEntityTypes().Select(t => t.ClrType).Where(typeof(ITenantEntity).IsAssignableFrom).ToList();
        Assert.NotEmpty(tenantTypes);

        foreach (var type in tenantTypes)
        {
            var (filtered, unfiltered) = await (Task<(int, int)>)CountMethod.MakeGenericMethod(type).Invoke(null, [ctx])!;
            Assert.True(unfiltered > 0, $"Seed a {type.Name} row in this test so the null-workspace check means something.");
            Assert.True(filtered == 0, $"{type.Name}: {filtered} row(s) visible without a workspace.");
        }
    }

    [Fact]
    public async Task Super_admin_context_still_sees_platform_tables()
    {
        using var ctx = _db.CreateContext(null);
        Assert.Single(await ctx.Workspaces.ToListAsync());
    }

    [Fact]
    public async Task Check_constraint_rejects_a_normal_user_without_a_workspace()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Users.Add(new AppUser { Id = Guid.NewGuid(), UserName = "orphan@x.com", Email = "orphan@x.com", WorkspaceId = null, PlatformRole = PlatformRole.None });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Contains("CK_AspNetUsers_WorkspaceOrSuperAdmin", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task Check_constraint_allows_a_workspaceless_super_admin()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Users.Add(new AppUser { Id = Guid.NewGuid(), UserName = "root@x.com", Email = "root@x.com", WorkspaceId = null, PlatformRole = PlatformRole.SuperAdmin });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Workspace_names_are_unique()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Workspaces.Add(new Workspace { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }
}
