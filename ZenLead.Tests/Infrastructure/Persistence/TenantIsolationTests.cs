using Microsoft.EntityFrameworkCore;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Persistence;
using ZenLead.Tests.Support;

namespace ZenLead.Tests.Infrastructure.Persistence;

public class TenantIsolationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly Guid _aLead = Guid.NewGuid();
    private readonly Guid _aDeletedLead = Guid.NewGuid();

    public TenantIsolationTests()
    {
        using var ctx = _db.CreateContext(null);
        foreach (var ws in new[] { _a, _b })
        {
            ctx.Workspaces.Add(new Workspace { Id = ws, Name = ws.ToString(), CreatedAt = DateTime.UtcNow });
            ctx.Companies.Add(new Company { Id = Guid.NewGuid(), WorkspaceId = ws, Name = "Co", Domain = "co.com", CreatedAt = DateTime.UtcNow });
            ctx.AiUsageLogs.Add(new AiUsageLog { Id = Guid.NewGuid(), WorkspaceId = ws, Model = "m", CreatedAt = DateTime.UtcNow });
        }
        ctx.Leads.Add(Lead(_aLead, _a, "a1@x.com"));
        ctx.Leads.Add(Lead(Guid.NewGuid(), _b, "b1@x.com"));
        var deleted = Lead(_aDeletedLead, _a, "a2@x.com");
        deleted.DeletedAt = DateTime.UtcNow;
        ctx.Leads.Add(deleted);
        ctx.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static Lead Lead(Guid id, Guid ws, string email) => new()
    {
        Id = id, WorkspaceId = ws, Name = "n", Email = email, Status = LeadStatus.New, CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task ScopedContext_SeesOnlyItsOwnRows()
    {
        using var ctx = _db.CreateContext(_a);

        Assert.All(await ctx.Leads.ToListAsync(), l => Assert.Equal(_a, l.WorkspaceId));
        Assert.Single(await ctx.Leads.ToListAsync());          // soft-deleted a2 hidden too
        Assert.All(await ctx.Companies.ToListAsync(), c => Assert.Equal(_a, c.WorkspaceId));
        Assert.Single(await ctx.Companies.ToListAsync());
        Assert.Single(await ctx.AiUsageLogs.ToListAsync());
    }

    [Fact]
    public async Task OtherWorkspace_CannotLoadALeadById()
    {
        using var ctx = _db.CreateContext(_b);
        Assert.Null(await new LeadRepository(ctx).GetByIdAsync(_aLead));
    }

    [Fact]
    public async Task NoWorkspace_SeesNothingInAnyTenantTable()
    {
        using var ctx = _db.CreateContext(null);

        Assert.Empty(await ctx.Leads.ToListAsync());
        Assert.Empty(await ctx.Companies.ToListAsync());
        Assert.Empty(await ctx.AiUsageLogs.ToListAsync());
    }

    [Fact]
    public async Task ForWorkspace_ScopesExplicitly_AndKeepsSoftDeleteFilter()
    {
        using var ctx = _db.CreateContext(null);

        var leads = await ctx.Leads.ForWorkspace(_a).ToListAsync();

        var lead = Assert.Single(leads);
        Assert.Equal(_aLead, lead.Id);
    }

    [Fact]
    public async Task UniqueEmail_IsPerWorkspace()
    {
        using (var ctx = _db.CreateContext(null))
        {
            ctx.Leads.Add(Lead(Guid.NewGuid(), _b, "a1@x.com"));   // same email as A's lead, different workspace
            await ctx.SaveChangesAsync();
        }

        using var dup = _db.CreateContext(null);
        dup.Leads.Add(Lead(Guid.NewGuid(), _a, "a1@x.com"));
        await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync());
    }

    [Fact]
    public async Task UniqueEmail_CoversSoftDeletedRows()
    {
        using var ctx = _db.CreateContext(null);
        ctx.Leads.Add(Lead(Guid.NewGuid(), _a, "a2@x.com"));   // a2 exists but is soft-deleted
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_StampsCurrentWorkspace_OrThrowsWithoutOne()
    {
        using (var ctx = _db.CreateContext(_b))
        {
            var lead = Lead(Guid.NewGuid(), Guid.Empty, "stamped@x.com");
            ctx.Leads.Add(lead);
            await ctx.SaveChangesAsync();
            Assert.Equal(_b, lead.WorkspaceId);
        }

        using var none = _db.CreateContext(null);
        none.Leads.Add(Lead(Guid.NewGuid(), Guid.Empty, "orphan@x.com"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.SaveChangesAsync());
    }

    [Fact]
    public void EveryEntityWithAWorkspaceId_IsATenantEntity()
    {
        using var ctx = _db.CreateContext(null);

        var offenders = ctx.Model.GetEntityTypes()
            .Where(t => t.ClrType != typeof(AppUser) && t.FindProperty("WorkspaceId") is not null)
            .Where(t => !typeof(ITenantEntity).IsAssignableFrom(t.ClrType))
            .Select(t => t.ClrType.Name)
            .ToList();

        Assert.Empty(offenders);
    }
}
